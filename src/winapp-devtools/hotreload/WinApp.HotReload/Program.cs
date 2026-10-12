// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace WinApp.HotReload;

/// <summary>
/// Watches a running app's project and applies each save to the app. Spawned by 'winapp run --watch'.
/// Prints one '[watch] &lt;outcome&gt; ...' line per save; exits with code 3 when the app must be rebuilt and restarted.
/// </summary>
internal static class Program
{
    public const int RestartExitCode = 3;

    public static async Task<int> Main(string[] args)
    {
        var options = Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("usage: WinApp.HotReload --project <csproj> --compiler-args <file> --targets <file> --pid <pid> --pipe <name> --token <token>");
            return 2;
        }
        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Value.Session.ProjectFile))!;
            SdkRoslyn.Register(SdkRoslyn.ResolveSdkDirectory(projectDirectory));
            return await RunAsync(options.Value.Session, options.Value.Pid).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[watch] error hot reload stopped: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static (SessionOptions Session, int Pid)? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            values[args[i]] = args[i + 1];
        }
        return values.TryGetValue("--project", out var project) && values.TryGetValue("--compiler-args", out var compilerArgs) &&
            values.TryGetValue("--targets", out var targets) && values.TryGetValue("--pipe", out var pipe) &&
            values.TryGetValue("--token", out var token) && values.TryGetValue("--pid", out var pid) && int.TryParse(pid, out var processId)
            ? (new SessionOptions(project, compilerArgs, targets, pipe, token), processId)
            : null;
    }

    // Kept out of Main so no Roslyn type is loaded before the SDK resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunAsync(SessionOptions options, int pid)
    {
        using var cts = new CancellationTokenSource();
        using var app = Process.GetProcessById(pid);
        _ = app.WaitForExitAsync().ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var stopwatch = Stopwatch.StartNew();
        var session = await HotReloadSession.CreateAsync(options, cts.Token).ConfigureAwait(false);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changes = Channel.CreateUnbounded<string>();
        // The app's folder, and those of the projects it references outside it (added as they are referenced).
        var watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        void WatchRoots()
        {
            foreach (var root in session.WatchRoots.Where(r => !watchers.ContainsKey(r) && Directory.Exists(r)))
            {
                lock (hashes)
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => IsWatched(session, f)))
                    {
                        hashes[file] = Hash(file);
                    }
                }
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += (_, e) => Enqueue(e.FullPath);
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Deleted += (_, e) => { Enqueue(e.FullPath); EnqueueFolder(e.FullPath, null); };
                watcher.Renamed += (_, e) =>
                {
                    Enqueue(e.OldFullPath);
                    Enqueue(e.FullPath);
                    if (Directory.Exists(e.FullPath))
                    {
                        EnqueueFolder(e.OldFullPath, e.FullPath);
                    }
                };
                watcher.EnableRaisingEvents = true;
                watchers[root] = watcher;
            }
            // The Directory.Build files of the folders above: only those files, not the folders' contents.
            foreach (var folder in session.BuildFileFolders().Where(f => !watchers.ContainsKey(f) && Directory.Exists(f)))
            {
                lock (hashes)
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "Directory.*").Where(f => IsWatched(session, f)))
                    {
                        hashes[file] = Hash(file);
                    }
                }
                var watcher = new FileSystemWatcher(folder, "Directory.*")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += (_, e) => Enqueue(e.FullPath);
                watcher.Created += (_, e) => Enqueue(e.FullPath);
                watcher.Deleted += (_, e) => Enqueue(e.FullPath);
                watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
                watcher.EnableRaisingEvents = true;
                watchers[folder] = watcher;
            }
        }
        void Enqueue(string path)
        {
            if (IsWatched(session, path))
            {
                changes.Writer.TryWrite(path);
            }
        }
        // A folder renamed, moved or deleted: every file in it moved or went away.
        void EnqueueFolder(string? oldFolder, string? newFolder)
        {
            if (oldFolder is not null)
            {
                lock (hashes)
                {
                    foreach (var file in hashes.Keys.Where(k => k.StartsWith(oldFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        Enqueue(file);
                    }
                }
            }
            if (newFolder is not null && Directory.Exists(newFolder))
            {
                foreach (var file in Directory.EnumerateFiles(newFolder, "*", SearchOption.AllDirectories))
                {
                    Enqueue(file);
                }
            }
        }
        WatchRoots();
        // The running binary was compiled when the compiler arguments were written; a save after that isn't in it.
        var built = File.GetLastWriteTimeUtc(options.CscArgsFile);
        // Saves made while the app was starting: the session diffs them against the sources as built (see AsBuilt).
        foreach (var late in hashes.Keys.Where(file => File.GetLastWriteTimeUtc(file) > built).ToList())
        {
            changes.Writer.TryWrite(late);
            hashes[late] = "";
        }
        Console.WriteLine($"[watch] ready ({stopwatch.ElapsedMilliseconds} ms): saved .cs, .xaml and project file changes apply to the running app (PID {pid})");
        if (session.LaunchNote is { } note)
        {
            Console.WriteLine($"[watch] note: {note}");
        }
        // Files from a save that reported an error are processed again with the next save, so nothing in that batch is lost.
        var retry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            while (true)
            {
                var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { await changes.Reader.ReadAsync(cts.Token).ConfigureAwait(false) };
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                {
                    while (true)
                    {
                        quiet.CancelAfter(150);
                        try
                        {
                            batch.Add(await changes.Reader.ReadAsync(quiet.Token).ConfigureAwait(false));
                            quiet.TryReset();
                        }
                        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }

                List<string> changed;
                lock (hashes)
                {
                    changed = batch.Where(path =>
                    {
                        var hash = File.Exists(path) ? Hash(path) : "";
                        var differs = !hashes.TryGetValue(path, out var previous) ? hash.Length > 0 : previous != hash;
                        hashes[path] = hash;
                        return differs;
                    }).ToList();
                }
                if (changed.Count == 0)
                {
                    continue;
                }
                changed = changed.Union(retry, StringComparer.OrdinalIgnoreCase).ToList();

                stopwatch.Restart();
                SaveResult result;
                try
                {
                    result = await session.ProcessAsync(changed, cts.Token).ConfigureAwait(false);
                }
                catch (CompilerCrashException ex)
                {
                    Console.Error.WriteLine(ex.InnerException);
                    if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
                    {
                        File.AppendAllText(log + ".helper", $"{DateTime.Now:HH:mm:ss.fff} {ex.InnerException}{Environment.NewLine}");
                    }
                    result = new SaveResult(Outcome.Restart, $"the compiler couldn't express this change as an update ({ex.Message})");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine(ex);
                    if (Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG") is { Length: > 0 } log)
                    {
                        File.AppendAllText(log + ".helper", $"{DateTime.Now:HH:mm:ss.fff} {ex}{Environment.NewLine}");
                    }
                    result = new SaveResult(Outcome.Error, $"internal error: {ex.Message}");
                }
                retry = new HashSet<string>(result.Outcome == Outcome.Error ? changed : [], StringComparer.OrdinalIgnoreCase);
                WatchRoots();
                var outcome = result.Outcome.ToString().ToLowerInvariant();
                Console.WriteLine($"[watch] {outcome} {result.Detail} ({stopwatch.ElapsedMilliseconds} ms)");
                if (result.Outcome == Outcome.Restart)
                {
                    return RestartExitCode;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            foreach (var watcher in watchers.Values)
            {
                watcher.Dispose();
            }
            try
            {
                Directory.Delete(Path.Combine(Path.GetTempPath(), "winapp-hotreload", options.PipeName), recursive: true);
            }
            catch (IOException)
            {
                // Markup files can still be mapped by an app that is shutting down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsWatched(HotReloadSession session, string path) => session.IsBuildFile(path) ||
        (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".resw", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".appxmanifest", StringComparison.OrdinalIgnoreCase) ||
         HotReloadSession.IsAsset(path)) && session.IsWatched(path);

    private static string Hash(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (FileNotFoundException)
            {
                return "";
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(30);
            }
        }
    }
}
