// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>
/// The "compiler" pipeline (WINAPP_HOT_RELOAD_PIPELINE=compiler): a saved XAML file is compiled by the WinUI XAML compiler
/// itself, kept warm in its own process. Its generated C# replaces the build's in the workspace and goes to the app as a
/// .NET hot reload delta like any code change; its .xbf goes to WinUI, and InitializeComponent runs again, so the app
/// runs exactly the code and markup a build of the saved files would produce. Nothing is rewritten.
/// </summary>
internal sealed partial class HotReloadSession
{
    /// <summary>"rewrite" (the XAML is rewritten and wired by winapp) or "compiler".</summary>
    public static string PipelineName { get; } = (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_PIPELINE") is { Length: > 0 } pipeline ? pipeline : "rewrite").ToLowerInvariant();

    /// <summary>How live pages show a save on the compiler pipeline: inplace (the default), recreate or shadow.</summary>
    public static string PageUpdateName { get; } = (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_PAGE_UPDATE") ?? "").ToLowerInvariant();

    public static bool UseCompiler => PipelineName == "compiler";

    private XamlCompilerHost? _xaml;
    private Task _xamlReady = Task.CompletedTask;

    /// <summary>Why XAML saves restart the app on this pipeline (no compiler service, no compiler inputs); null when they don't.</summary>
    private string? _xamlUnavailable;

    /// <summary>The XAML compiler's output folder in obj (where the build put XamlTypeInfo.g.cs and the .g.cs files).</summary>
    private string _xamlIntermediate = "";

    /// <summary>The XAML files the compiler was given by the build, by full path.</summary>
    private HashSet<string> _xamlInputs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's assembly (metadata only) for pass 2, when its C# changed since the build; null reads the build's.</summary>
    private byte[]? _xamlImage;
    private bool _xamlImageStale;

    private static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The documents' text as the running binary was built from, pinned: a build may overwrite the files in obj.</summary>
    private static DocumentInfo Pinned(DocumentInfo document)
    {
        if (document.FilePath is not { } path || !IsGenerated(path) || !File.Exists(path))
        {
            return document;
        }
        using var stream = File.OpenRead(path);
        var text = SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);
        return document.WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path)));
    }

    /// <summary>Starts the XAML compiler and warms it up in the background (the first save waits for it).</summary>
    private void StartXamlCompiler()
    {
        _xamlIntermediate = Project.Documents.Select(d => d.FilePath).OfType<string>()
            .FirstOrDefault(p => p.EndsWith("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase)) is { } typeInfo ? Path.GetDirectoryName(typeInfo)! : "";
        var inputs = _xamlIntermediate.Length > 0 ? Path.Combine(_xamlIntermediate, "input.json") : "";
        if (!File.Exists(inputs))
        {
            _xamlUnavailable = "the XAML compiler's inputs from the app's build (input.json next to XamlTypeInfo.g.cs) weren't found";
            return;
        }
        if (XamlCompilerHost.Locate() is not { } exe)
        {
            _xamlUnavailable = $"the XAML compiler service wasn't found (set {XamlCompilerHost.LocationVariable} to a folder with XamlCompiler.exe that supports --serve)";
            return;
        }
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(inputs));
            foreach (var list in new[] { "XamlApplications", "XamlPages" })
            {
                if (json.RootElement.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("ItemSpec", out var spec) && spec.GetString() is { } file)
                        {
                            _xamlInputs.Add(Path.GetFullPath(Path.Combine(_projectDirectory, file)));
                        }
                    }
                }
            }
            _xaml = XamlCompilerHost.Start(exe, inputs, Path.Combine(_markupDirectory, "xaml"), _projectDirectory);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _xamlUnavailable = "the XAML compiler service couldn't start: " + ex.Message;
            return;
        }
        _xamlReady = Task.Run(WarmUpXamlCompilerAsync);
    }

    /// <summary>
    /// A full compile, as the build ran it: loads the compiler's references once, and records which types XamlTypeInfo
    /// has, so a later page compile can tell when it needs regenerating.
    /// </summary>
    private async Task WarmUpXamlCompilerAsync()
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var pass1 = await _xaml!.Pass1Async(null, CancellationToken.None).ConfigureAwait(false);
            var pass2 = await _xaml.Pass2Async(null, null, typeInfo: true, CancellationToken.None).ConfigureAwait(false);
            if (!pass1.Success || !pass2.Success)
            {
                _xamlUnavailable = "the XAML compiler service couldn't compile the app as built: " +
                    string.Join("; ", pass1.Diagnostics.Concat(pass2.Diagnostics).Where(d => d.IsError).Take(3).Select(FormatXaml));
                return;
            }
            // The service's output should match the build's byte for byte; a difference becomes a change in the first update.
            var differs = new List<string>();
            foreach (var (path, text) in pass1.GeneratedText.Concat(pass2.GeneratedText).Where(p => IsGenerated(p.Key)))
            {
                var document = Project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, InIntermediate(path), StringComparison.OrdinalIgnoreCase));
                if (document is not null && !(await document.GetTextAsync().ConfigureAwait(false)).ToString().TrimStart('\uFEFF').TrimEnd().Equals(text.TrimStart('\uFEFF').TrimEnd(), StringComparison.Ordinal))
                {
                    differs.Add(Path.GetFileName(path));
                }
            }
            Log($"XAML compiler warm in {watch.ElapsedMilliseconds} ms ({pass1.ElapsedMs:0} + {pass2.ElapsedMs:0} ms); differs from the build: {(differs.Count == 0 ? "nothing" : string.Join(", ", differs))}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _xamlUnavailable = "the XAML compiler service stopped: " + ex.Message;
        }
    }

    /// <summary>A path in the compiler's output folder, as the same file in the build's obj folder.</summary>
    private string InIntermediate(string outputPath) =>
        Path.GetFullPath(Path.Combine(_xamlIntermediate, Path.GetRelativePath(_xaml!.OutputDirectory, outputPath)));

    private string FormatXaml(XamlCompilerDiagnostic d) =>
        $"{(d.File is { Length: > 0 } file ? Relative(Path.GetFullPath(Path.Combine(_projectDirectory, file))) : Path.GetFileName(_options.ProjectFile))}" +
        $"{(d.Line > 0 ? $"({d.Line},{d.Column})" : "")}: {(d.IsError ? "error" : "warning")} {d.Code}: {d.Message}";

    private static void Log(string text)
    {
        if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
        {
            File.AppendAllText(log + ".helper", $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
    }

    /// <summary>What compiling a save's XAML gives the update.</summary>
    private sealed record XamlCompiled(List<MarkupPayload> Markup, List<string> Classes);

    /// <summary>
    /// Compiles the saved XAML files with the XAML compiler and puts its generated C# in the workspace. Returns the
    /// markup to send, or the save's result when it can't apply (a compile error, a change the running app can't take).
    /// </summary>
    private async Task<(XamlCompiled? Compiled, SaveResult? Result)> CompileXamlAsync(IReadOnlyList<string> xaml, bool codeChanged, CancellationToken cancellationToken)
    {
        _xamlImageStale |= codeChanged;
        var compiled = new XamlCompiled([], []);
        if (xaml.Count == 0)
        {
            return (compiled, null);
        }
        await _xamlReady.ConfigureAwait(false);
        if (_xamlUnavailable is not null || _xaml is null)
        {
            return (null, new SaveResult(Outcome.Restart, $"{Relative(xaml[0])}: {_xamlUnavailable ?? "the XAML compiler service isn't running"}; XAML changes take effect when the app restarts"));
        }
        if (xaml.FirstOrDefault(p => !_xamlInputs.Contains(p)) is { } added)
        {
            return (null, new SaveResult(Outcome.Restart, $"{Relative(added)} is new since the app was built: a new XAML file takes effect when the app is rebuilt"));
        }
        foreach (var path in xaml)
        {
            try
            {
                XDocument.Parse(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (System.Xml.XmlException ex)
            {
                // A half-typed tag: the compiler would only say the same thing less precisely.
                return (null, new SaveResult(Outcome.Error, XmlError(path, ex)));
            }
        }

        var pass1 = await _xaml.Pass1Async(xaml, cancellationToken).ConfigureAwait(false);
        if (Failed(pass1) is { } pass1Error)
        {
            return (null, pass1Error);
        }
        if (await UseGeneratedAsync(pass1, cancellationToken).ConfigureAwait(false) is { } fieldChange)
        {
            return (null, fieldChange);
        }

        // Pass 2 reads the app's own types (x:Bind paths, event handlers): after a C# change, from the current code.
        if (_xamlImageStale)
        {
            var compilation = (await Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            using var image = new MemoryStream();
            var emit = compilation.Emit(image, options: new EmitOptions(metadataOnly: true, includePrivateMembers: true), cancellationToken: cancellationToken);
            if (!emit.Success)
            {
                return (null, new SaveResult(Outcome.Error, string.Join(Environment.NewLine + "  ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).Select(Format))));
            }
            _xamlImage = image.ToArray();
            _xamlImageStale = false;
        }
        var pass2 = await _xaml.Pass2Async(xaml, _xamlImage, typeInfo: false, cancellationToken).ConfigureAwait(false);
        if (Failed(pass2) is { } pass2Error)
        {
            return (null, pass2Error);
        }
        if (pass2.XamlTypeInfoMayBeStale)
        {
            // The pages use a type or member XamlTypeInfo doesn't have yet: regenerated as a build does, from every page.
            var full = await _xaml.Pass2Async(null, _xamlImage, typeInfo: true, cancellationToken).ConfigureAwait(false);
            if (Failed(full) is { } fullError)
            {
                return (null, fullError);
            }
            await UseGeneratedAsync(full, cancellationToken).ConfigureAwait(false);
        }
        await UseGeneratedAsync(pass2, cancellationToken).ConfigureAwait(false);

        foreach (var path in xaml)
        {
            var xbf = Path.ChangeExtension(Path.Combine(_xaml.OutputDirectory, Path.GetRelativePath(_projectDirectory, path)), ".xbf");
            if (!pass2.GeneratedXbf.TryGetValue(xbf, out var bytes))
            {
                return (null, new SaveResult(Outcome.Restart, $"{Relative(path)}: the XAML compiler produced no .xbf for it; it takes effect when the app restarts"));
            }
            var root = XDocument.Parse(await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false)).Root!;
            var className = root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
            var kind = root.Name.LocalName == "Application" ? MarkupKind.Application
                : className is not null ? MarkupKind.Component
                : Relative(path).Replace('/', '\\').EndsWith(@"Themes\Generic.xaml", StringComparison.OrdinalIgnoreCase) ? MarkupKind.Generic
                : MarkupKind.Dictionary;
            compiled.Markup.Add(new MarkupPayload { Kind = kind, Uri = UriFor(path), Xbf = bytes, Manifest = new ComponentManifest { ClassName = className ?? "" } });
            _sources[UriFor(path)] = path;
            if (kind == MarkupKind.Component)
            {
                compiled.Classes.Add(className!);
            }
        }
        Log($"XAML compiled: pass 1 {pass1.ElapsedMs:0} ms, pass 2 {pass2.ElapsedMs:0} ms{(pass2.XamlTypeInfoMayBeStale ? ", XamlTypeInfo regenerated" : "")}");
        return (compiled, null);
    }

    private SaveResult? Failed(XamlCompileOutput output) =>
        output.Success && !output.Diagnostics.Any(d => d.IsError) ? null
            : new SaveResult(Outcome.Error, output.Diagnostics.Any(d => d.IsError)
                ? string.Join(Environment.NewLine + "  ", output.Diagnostics.Where(d => d.IsError).Take(3).Select(FormatXaml))
                : "the XAML compiler failed without saying why");

    /// <summary>
    /// The compiler's generated C# as the workspace's documents (the build's, in obj). A named element whose type changed
    /// is refused: edit and continue never compares the .g.i.cs files that declare those fields (it treats them as
    /// design-time only), so it wouldn't report that the running app can't change a field's type.
    /// </summary>
    private async Task<SaveResult?> UseGeneratedAsync(XamlCompileOutput output, CancellationToken cancellationToken)
    {
        foreach (var (outputPath, generated) in output.GeneratedText.Where(p => IsGenerated(p.Key)))
        {
            var path = InIntermediate(outputPath);
            var text = SourceText.From(WithoutRedundantStaticInitializers(generated.TrimStart('\uFEFF')), Encoding.UTF8, SourceHashAlgorithm.Sha256);
            var ids = _solution.GetDocumentIdsWithFilePath(path);
            if (ids.IsEmpty)
            {
                _solution = _solution.AddDocument(DocumentId.CreateNewId(_projectId), Path.GetFileName(path), text, filePath: path);
                continue;
            }
            var before = await _solution.GetDocument(ids[0])!.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (before.ContentEquals(text))
            {
                continue;
            }
            if (path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) && FieldTypeChange(before.ToString(), text.ToString()) is { } change)
            {
                return new SaveResult(Outcome.Restart, $"{Relative(XamlFor(path))}: {change}, and the running app can't change the type of a field; it takes effect when the app restarts");
            }
            _solution = ids.Aggregate(_solution, (s, id) => s.WithDocumentText(id, text));
        }
        return null;
    }

    /// <summary>"x:Name 'Later' changes from TextBlock to TextBox", for the first named field whose type changes.</summary>
    private static string? FieldTypeChange(string before, string after)
    {
        static Dictionary<string, string> Fields(string text) => CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables.Select(v => (v.Identifier.ValueText, f.Declaration.Type.ToString())))
            .GroupBy(f => f.Item1).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal);
        var old = Fields(before);
        foreach (var (name, type) in Fields(after))
        {
            if (old.TryGetValue(name, out var was) && was != type)
            {
                return $"x:Name '{name}' changes from {Short(was)} to {Short(type)}";
            }
        }
        return null;
        static string Short(string type) => type[(type.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// "private static bool isobj9TextDisabled = false;" becomes "...isobj9TextDisabled;", which is the same: Roslyn 5.9
    /// fails (NullReferenceException in DefinitionMap.GetPreviousMethodHandle) on an update that adds an initialized static
    /// field to a type an earlier update added. Until the XAML compiler stops emitting the redundant initializer.
    /// </summary>
    private static string WithoutRedundantStaticInitializers(string text) =>
        Regex.Replace(text, @"(private static bool isobj\w+Disabled) = false;", "$1;");

    /// <summary>The XAML file a generated file in obj was generated from (Pages\Foo.g.cs: Pages\Foo.xaml).</summary>
    private string XamlFor(string generatedPath)
    {
        var relative = Path.GetRelativePath(_xamlIntermediate, generatedPath);
        var stem = relative.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ? relative[..^".g.i.cs".Length] : relative[..^".g.cs".Length];
        return Path.Combine(_projectDirectory, stem + ".xaml");
    }

    private bool IsXamlGenerated(string? path) =>
        path is not null && _xamlIntermediate.Length > 0 && IsGenerated(path) && path.StartsWith(_xamlIntermediate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A rude edit in plain words. In the XAML compiler's code it is said against the XAML file: what the compiled code
    /// for the change does that the running app can't (ENC0033 "Deleting field 'obj18'" is "it deletes field 'obj18'").
    /// </summary>
    private string RudeEditReason(IReadOnlyList<Diagnostic> rude)
    {
        var first = rude[0];
        var path = first.Location.SourceTree?.FilePath;
        if (!IsXamlGenerated(path))
        {
            return Format(first);
        }
        var message = first.GetMessage();
        var what = Regex.Replace(message, @"\s*requires restarting the application\.?$", "");
        what = what.Length > 0 ? char.ToLowerInvariant(what[0]) + what[1..] : message;
        var more = rude.Count > 1 ? $" (and {rude.Count - 1} more)" : "";
        return $"{Relative(XamlFor(path!))}: the compiled code for this change does something the running app can't: {what} ({first.Id}){more}; it takes effect when the app restarts";
    }
}
