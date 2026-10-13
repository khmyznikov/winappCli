// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class ProjectRunService
{
    private const string CustomAfterCommonTargetsProperty = "CustomAfterMicrosoftCommonTargets";

    /// <summary>
    /// MSBuild targets that <c>winapp run</c> imports into its build (through
    /// <c>CustomAfterMicrosoftCommonTargets</c>) so Windows App SDK packaging sees project references the
    /// way the rest of the build does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a reference that doesn't depend on a runtime identifier (a typical class library), the .NET SDK
    /// marks the prepared reference with <c>UndefineProperties=RuntimeIdentifier</c>, so the library builds
    /// once, without the RID. The MSIX/PRI packaging targets query the raw <c>ProjectReferenceWithConfiguration</c>
    /// items instead, which lack that marker, so they build the library a second time with the global
    /// <c>-r</c> into a <c>win-&lt;arch&gt;</c> subfolder. Packaging then finds two copies (APPX1101) or looks
    /// for a file that was never written (PRI175/PRI252, MSB3030). The first target hands the packaging
    /// targets the prepared items, so both queries agree.
    /// </para>
    /// <para>
    /// After a build failed that way, the app's cached PRI input list (<c>pri.resfiles</c>) still names the
    /// missing file, and the PRI targets only regenerate it when their inputs change. The second target
    /// deletes that list when it names a file that no longer exists.
    /// </para>
    /// <para>
    /// Setting the property on the command line hides the machine-wide default file it normally names, so
    /// that file is imported here when present.
    /// </para>
    /// </remarks>
    internal const string PackagingReferenceTargets = """
        <Project>
          <!-- Written by winapp for `winapp run`. See ProjectRunService.PackagingReferences.cs. -->
          <Import Project="$(MSBuildExtensionsPath)\v$(MSBuildToolsVersion)\Custom.After.Microsoft.Common.targets"
                  Condition="Exists('$(MSBuildExtensionsPath)\v$(MSBuildToolsVersion)\Custom.After.Microsoft.Common.targets')" />

          <Target Name="WinappAlignPackagingReferences"
                  AfterTargets="PrepareProjectReferences"
                  Condition="'@(_MSBuildProjectReferenceExistent)' != ''">
            <ItemGroup>
              <ProjectReferenceWithConfiguration Remove="@(ProjectReferenceWithConfiguration)" />
              <ProjectReferenceWithConfiguration Include="@(_MSBuildProjectReferenceExistent)">
                <UndefineProperties>%(_MSBuildProjectReferenceExistent.UndefineProperties);%(_MSBuildProjectReferenceExistent.GlobalPropertiesToRemove)</UndefineProperties>
              </ProjectReferenceWithConfiguration>
            </ItemGroup>
          </Target>

          <Target Name="WinappDropStalePriInputs"
                  BeforeTargets="_GenerateProjectPriConfigurationFiles"
                  Condition="'$(_PriResfilesPath)' != '' and Exists('$(_PriResfilesPath)')">
            <ReadLinesFromFile File="$(_PriResfilesPath)">
              <Output TaskParameter="Lines" ItemName="_WinappPriInput" />
            </ReadLinesFromFile>
            <ItemGroup>
              <_WinappMissingPriInput Include="@(_WinappPriInput)" Condition="!Exists('%(Identity)')" />
            </ItemGroup>
            <Delete Files="$(_PriResfilesPath)" Condition="'@(_WinappMissingPriInput)' != ''" />
          </Target>
        </Project>
        """;

    private static readonly string[] CustomAfterProbeProperties = [CustomAfterCommonTargetsProperty, "MSBuildExtensionsPath", "MSBuildToolsVersion"];

    /// <summary>
    /// Returns the path of the <see cref="PackagingReferenceTargets"/> file to import into a
    /// <c>winapp run</c> build, writing it if needed, or <see langword="null"/> to build exactly as before.
    /// A command-line value would replace one the build already sets, so this returns
    /// <see langword="null"/> when the user passes the property, when the app's evaluated value (from its
    /// project, imports, environment or response files) is anything but MSBuild's default, when a
    /// referenced project or a <c>Directory.Build.*</c> above it names the property, and when the app
    /// can't be evaluated. <paramref name="evaluated"/> reuses an earlier evaluation of the app, when one ran.
    /// </summary>
    private async Task<string?> ResolvePackagingReferenceTargetsAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        IReadOnlyDictionary<string, string>? evaluated,
        DirectoryInfo workingDir,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        if (UserSpecifiesProperty(options.Properties, CustomAfterCommonTargetsProperty))
        {
            return null;
        }

        evaluated ??= await TryEvaluateCustomAfterPropertiesAsync(csproj, options, workingDir, csWinRTMetadataFolder, cancellationToken);
        if (evaluated is null)
        {
            return null;
        }

        if (!IsDefaultCustomAfterCommonTargets(evaluated) || ProjectGraphMentionsProperty(csproj, CustomAfterCommonTargetsProperty))
        {
            logger.LogDebug("{UISymbol} The build sets {Property}; not importing winapp's packaging-reference targets.", UiSymbols.Note, CustomAfterCommonTargetsProperty);
            return null;
        }

        return WritePackagingReferenceTargets();
    }

    private async Task<IReadOnlyDictionary<string, string>?> TryEvaluateCustomAfterPropertiesAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        var args = BuildEvaluateArguments(csproj, options, csWinRTMetadataFolder, properties: CustomAfterProbeProperties);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));
        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            return exitCode == 0 ? MsBuildPropertyReader.Parse(stdout, CustomAfterProbeProperties) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the evaluated <c>CustomAfterMicrosoftCommonTargets</c> is empty or MSBuild's default
    /// machine-wide path, which <see cref="PackagingReferenceTargets"/> imports itself.
    /// </summary>
    internal static bool IsDefaultCustomAfterCommonTargets(IReadOnlyDictionary<string, string> evaluated)
    {
        var value = evaluated.TryGetValue(CustomAfterCommonTargetsProperty, out var v) ? v.Trim() : string.Empty;
        if (value.Length == 0)
        {
            return true;
        }

        var extensions = evaluated.TryGetValue("MSBuildExtensionsPath", out var e) ? e : string.Empty;
        var toolsVersion = evaluated.TryGetValue("MSBuildToolsVersion", out var t) ? t : string.Empty;
        if (extensions.Length == 0)
        {
            return false;
        }

        // MSBuild concatenates the default as text ("…\sdk\10.0.401\\vCurrent\…"), so compare normalized paths.
        try
        {
            var defaultPath = Path.GetFullPath(Path.Join(extensions, $"v{toolsVersion}", "Custom.After.Microsoft.Common.targets"));
            return string.Equals(Path.GetFullPath(value), defaultPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Writes <see cref="PackagingReferenceTargets"/> to a per-content temp path, or returns null.</summary>
    internal static string? WritePackagingReferenceTargets()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PackagingReferenceTargets)))[..12];
        var path = Path.Join(Path.GetTempPath(), "winapp", "msbuild", $"winapp-run-{hash}.targets");
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, PackagingReferenceTargets);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file the build behaves exactly as before, so a write failure isn't fatal.
            return null;
        }

        return path;
    }

    /// <summary>
    /// True when the project, a project it references (statically resolvable includes), or a
    /// <c>Directory.Build.props</c>/<c>Directory.Build.targets</c> above any of them names
    /// <paramref name="propertyName"/>. Referenced projects aren't evaluated, so this catches the common
    /// way a reference sets the property.
    /// </summary>
    internal static bool ProjectGraphMentionsProperty(FileInfo start, string propertyName)
    {
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.FullName };
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<FileInfo>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var project = queue.Dequeue();
            if (FileMentions(project.FullName, propertyName))
            {
                return true;
            }

            for (var directory = project.Directory; directory is not null && visitedDirectories.Add(directory.FullName); directory = directory.Parent)
            {
                if (FileMentions(Path.Join(directory.FullName, "Directory.Build.props"), propertyName)
                    || FileMentions(Path.Join(directory.FullName, "Directory.Build.targets"), propertyName))
                {
                    return true;
                }
            }

            var unvisitedReferences = ReadAllProjectReferenceIncludes(project)
                .Select(include => TryResolveReferencePath(project, include, out var resolved) ? resolved : null)
                .OfType<FileInfo>()
                .Where(reference => visitedProjects.Count < MaxProjectReferenceClosure && visitedProjects.Add(reference.FullName));
            foreach (var referenced in unvisitedReferences)
            {
                queue.Enqueue(referenced);
            }
        }

        return false;

        static bool FileMentions(string path, string text)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).Contains(text, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Every <c>ProjectReference</c> include in the project XML, build-only references included.</summary>
    private static List<string> ReadAllProjectReferenceIncludes(FileInfo project)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(project.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .SelectMany(include => include!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
    }
}
