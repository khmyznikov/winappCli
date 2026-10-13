// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class ProjectRunService
{
    /// <summary>
    /// MSBuild targets that <c>winapp run</c> writes next to the app's NuGet-generated files
    /// (<c>obj\&lt;project file&gt;.winapp.g.targets</c>). MSBuild imports every
    /// <c>$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).*.targets</c> file for that project only, so
    /// referenced projects and the build's own properties are untouched.
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
    /// </remarks>
    internal const string PackagingReferenceTargets = """
        <Project>
          <!-- Written by `winapp run`; safe to delete. Makes Windows App SDK packaging build each referenced library once. -->
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

    internal const string PackagingReferenceTargetsSuffix = ".winapp.g.targets";

    private const string ProjectExtensionsPathProperty = "MSBuildProjectExtensionsPath";

    private static readonly string[] ProjectExtensionsProbeProperties = [ProjectExtensionsPathProperty, "MSBuildProjectFile"];

    /// <summary>
    /// Writes <see cref="PackagingReferenceTargets"/> to the app's <c>$(MSBuildProjectExtensionsPath)</c> before a
    /// <c>winapp run</c> build and returns its path. Returns <see langword="null"/>, and the build runs exactly as
    /// before, when the app can't be evaluated or the file can't be written. <paramref name="evaluated"/> reuses an
    /// earlier evaluation of the app, when one ran.
    /// </summary>
    private async Task<string?> EnsurePackagingReferenceTargetsAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        IReadOnlyDictionary<string, string>? evaluated,
        DirectoryInfo workingDir,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        // An earlier evaluation of the app used the same inputs, so its answer (even an empty one) is final.
        var extensionsPath = evaluated is not null
            ? GetProp(evaluated, ProjectExtensionsPathProperty)
            : await TryEvaluateProjectExtensionsPathAsync(csproj, options, workingDir, csWinRTMetadataFolder, cancellationToken);
        if (string.IsNullOrWhiteSpace(extensionsPath))
        {
            return null;
        }

        var path = GetPackagingReferenceTargetsPath(csproj, extensionsPath);
        if (path is null || !TryWritePackagingReferenceTargets(path))
        {
            return null;
        }

        logger.LogDebug("{UISymbol} Packaging-reference targets: {Path}", UiSymbols.Note, path);
        return path;
    }

    /// <summary>
    /// The file MSBuild imports for <paramref name="csproj"/> from <paramref name="extensionsPath"/> (the evaluated
    /// <c>MSBuildProjectExtensionsPath</c>, absolute or relative to the project), or null for an invalid path.
    /// </summary>
    internal static string? GetPackagingReferenceTargetsPath(FileInfo csproj, string extensionsPath)
    {
        try
        {
            var directory = Path.GetFullPath(extensionsPath, csproj.DirectoryName ?? Directory.GetCurrentDirectory());
            return Path.Join(directory, csproj.Name + PackagingReferenceTargetsSuffix);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <see cref="PackagingReferenceTargets"/> to <paramref name="path"/> unless it already has that content,
    /// so an unchanged file keeps its timestamp and doesn't invalidate incremental builds.
    /// </summary>
    internal static bool TryWritePackagingReferenceTargets(string path)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == PackagingReferenceTargets)
            {
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, PackagingReferenceTargets);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string?> TryEvaluateProjectExtensionsPathAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        DirectoryInfo workingDir,
        string? csWinRTMetadataFolder,
        CancellationToken cancellationToken)
    {
        var args = BuildEvaluateArguments(csproj, options, csWinRTMetadataFolder, properties: ProjectExtensionsProbeProperties);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, RedactSecretsForDisplay(args));
        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDir, args, cancellationToken);
            return exitCode == 0
                ? GetProp(MsBuildPropertyReader.Parse(stdout, ProjectExtensionsProbeProperties), ProjectExtensionsPathProperty)
                : null;
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
}