// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace WinApp.HotReload;

/// <summary>Locates the installed SDK's compiler and hot reload service and makes them loadable.</summary>
internal static class SdkRoslyn
{
    public static void Register(string sdkDirectory)
    {
        var bincore = Path.Combine(sdkDirectory, "Roslyn", "bincore");
        var watchRoot = Path.Combine(sdkDirectory, "DotnetTools", "dotnet-watch");
        var watch = Directory.Exists(watchRoot)
            ? Directory.EnumerateFiles(watchRoot, "Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (!File.Exists(Path.Combine(bincore, "Microsoft.CodeAnalysis.dll")) || watch is null)
        {
            throw new InvalidOperationException($"The .NET SDK at '{sdkDirectory}' does not include the dotnet-watch hot reload service. Install .NET SDK 10.0.100 or later.");
        }
        string[] directories = [bincore, Path.GetDirectoryName(watch)!];
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(path))
                {
                    return context.LoadFromAssemblyPath(path);
                }
            }
            return null;
        };
    }

    public static string ResolveSdkDirectory(string projectDirectory)
    {
        var dotnetRoot = Path.GetDirectoryName(Environment.ProcessPath)!;
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--version")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var version = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        var directory = Path.Combine(dotnetRoot, "sdk", version);
        return Directory.Exists(directory) ? directory : throw new InvalidOperationException($"Could not locate the .NET SDK '{version}' under {dotnetRoot}.");
    }
}

internal sealed record EncDelta(Guid ModuleId, byte[] Metadata, byte[] IL, byte[] Pdb, int[] UpdatedTypes);

internal sealed record EncResult(string Status, IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> RudeEdits, IReadOnlyList<EncDelta> Deltas, bool NeedsRestart);

/// <summary>
/// Thin reflection wrapper over the SDK's own hot reload service (the engine behind dotnet watch). It computes
/// edit-and-continue deltas between the solution the app was built from and the current one, and reports rude edits.
/// </summary>
internal sealed class EncService
{
    private readonly object _service;
    private readonly Type _type;
    private readonly object _runningProjects;

    public EncService(Solution solution, ProjectId projectId, Func<ImmutableArray<string>> capabilities)
    {
        _type = Type.GetType("Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService, Microsoft.CodeAnalysis.ExternalAccess.HotReload", throwOnError: true)!;
        Func<ValueTask<ImmutableArray<string>>> provider = () => ValueTask.FromResult(capabilities());
        _service = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [solution.Services, provider], null)!;

        var infoType = _type.GetNestedType("RunningProjectInfo")!;
        var info = Activator.CreateInstance(infoType)!;
        var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(ProjectId), infoType);
        var pairs = Array.CreateInstance(pairType, 1);
        pairs.SetValue(Activator.CreateInstance(pairType, projectId, info), 0);
        _runningProjects = typeof(ImmutableDictionary).GetMethods()
            .First(method => method.Name == nameof(ImmutableDictionary.CreateRange) && method.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(ProjectId), infoType)
            .Invoke(null, [pairs])!;
    }

    public Task StartAsync(Solution solution, CancellationToken cancellationToken)
        => (Task)_type.GetMethod("StartSessionAsync")!.Invoke(_service, [solution, cancellationToken])!;

    public async Task<EncResult> GetUpdatesAsync(Solution solution, CancellationToken cancellationToken)
    {
        var task = (Task)_type.GetMethod("GetUpdatesAsync")!.Invoke(_service, [solution, _runningProjects, cancellationToken])!;
        await task.ConfigureAwait(false);
        var updates = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var updatesType = updates.GetType();
        object? Get(string name) => updatesType.GetProperty(name)!.GetValue(updates);

        var errors = ((ImmutableArray<Diagnostic>)Get("PersistentDiagnostics")!).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        LastWarnings = [.. ((ImmutableArray<Diagnostic>)Get("PersistentDiagnostics")!).Where(d => d.Severity != DiagnosticSeverity.Error)];
        var rude = new List<Diagnostic>();
        foreach (var entry in (IEnumerable)Get("TransientDiagnostics")!)
        {
            var diagnostics = (ImmutableArray<Diagnostic>)entry.GetType().GetField("Item2")!.GetValue(entry)!;
            rude.AddRange(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            LastWarnings.AddRange(diagnostics.Where(d => d.Severity != DiagnosticSeverity.Error));
        }
        var deltas = new List<EncDelta>();
        foreach (var update in (IEnumerable)Get("ProjectUpdates")!)
        {
            var updateType = update.GetType();
            T Field<T>(string name) => (T)updateType.GetField(name)!.GetValue(update)!;
            deltas.Add(new EncDelta(Field<Guid>("ModuleId"), [.. Field<ImmutableArray<byte>>("MetadataDelta")],
                [.. Field<ImmutableArray<byte>>("ILDelta")], [.. Field<ImmutableArray<byte>>("PdbDelta")], [.. Field<ImmutableArray<int>>("UpdatedTypes")]));
        }
        var restart = ((IDictionary)Get("ProjectsToRestart")!).Count > 0 || ((ICollection)Get("ProjectsToRebuild")!).Count > 0;
        return new EncResult(Get("Status")!.ToString()!, errors, rude, deltas, restart);
    }

    /// <summary>SPIKE: the warnings of the last update (ENC1005 marks documents out of sync with the build).</summary>
    public List<Diagnostic> LastWarnings { get; private set; } = [];

    public void CapabilitiesChanged() => _type.GetMethod("CapabilitiesChanged")!.Invoke(_service, null);

    public void Commit() => _type.GetMethod("CommitUpdate")!.Invoke(_service, null);

    public void Discard() => _type.GetMethod("DiscardUpdate")!.Invoke(_service, null);
}
