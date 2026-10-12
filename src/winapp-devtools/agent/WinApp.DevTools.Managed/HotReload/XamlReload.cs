// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WinApp.HotReload.Protocol;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Applies markup through WinUI's hot reload service (text XAML replaces compiled XBF per URI) and wires what the
/// runtime parser can't: x:Name fields, event handlers, root-scope x:Bind and Window properties. Then rebuilds the
/// live instances whose markup or constructor changed.
/// </summary>
internal static class XamlReload
{
    private const string ReplayMethod = "__WinAppHotReload_Construct";
    private const string GeneratedNamePrefix = "__hr_";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Dictionary<string, ComponentManifest> Manifests = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<WeakReference<Window>> KnownWindows = [];
    private static readonly ConditionalWeakTable<object, Dictionary<string, Delegate>> RootHandlers = new();
    private static XamlHotReloadService? s_service;
    private static DispatcherQueue? s_dispatcher;
    private static HashSet<object>? s_loadedDuringRefresh;
    private static List<string>? s_messages;
    private static List<string>? s_errors;
    private static ReflectionMetadataProvider? s_provider;
    private static bool s_initialized;

    public static bool Supported => s_service is not null;

    /// <summary>Why XAML can't be reloaded in this app although its WinUI could (it isn't enabled for hot reload).</summary>
    public static string? Unavailable { get; private set; }

    public static async Task<string?> InitializeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (FindDispatcher() is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                return "no WinUI window was found in the app";
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        if (s_initialized)
        {
            return null;
        }
        var (service, unavailable) = await XamlHotReloadService.AcquireAsync().ConfigureAwait(false);
        return await OnUiThreadAsync(() => Initialize(service, unavailable)).ConfigureAwait(false);
    }

    private static string? Initialize(XamlHotReloadService? service, string? unavailable)
    {
        if (s_initialized)
        {
            return null;
        }
        s_initialized = true;
        HotReloadAgent.UiThreadId = Environment.CurrentManagedThreadId;
        if (Application.Current is { } application)
        {
            // Recorded without handling it: the app goes down as it would without winapp.
            application.UnhandledException += (_, e) => HotReloadAgent.RecordCrash(e.Exception);
        }
        // Components that loaded before the agent connected: their current values are where they started.
        foreach (var window in LiveWindows())
        {
            UiState.CaptureBaseline(window.Content);
        }
        Unavailable = unavailable;
        if (service is null)
        {
            // C# edits still apply; XAML edits need a restart (a WinUI without hot reload, or an app not enabled for it).
            return null;
        }
        s_service = service;
        Hook.Attached = OnHook;
        service.AdviseComponentLoaded(OnLoaded);
        s_provider = new ReflectionMetadataProvider();
        var provider = WinRT.MarshalInterface<IXamlMetadataProvider>.FromManaged(s_provider);
        try
        {
            Marshal.ThrowExceptionForHR(service.RegisterMetadataProvider(provider));
        }
        finally
        {
            Marshal.Release(provider);
        }
        return null;
    }

    public static Task<T> OnUiThreadAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = FindDispatcher() ?? throw new InvalidOperationException("The app's UI thread is not available.");
        if (!dispatcher.TryEnqueue(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        }))
        {
            completion.SetException(new InvalidOperationException("The app's UI thread is shutting down."));
        }
        return completion.Task;
    }

    private static DispatcherQueue? FindDispatcher()
    {
        if (s_dispatcher is null && LiveWindows().FirstOrDefault() is { } window)
        {
            s_dispatcher = window.DispatcherQueue;
        }
        return s_dispatcher;
    }

    /// <summary>Windows held by the app (fields of the Application or statics of the app's types) plus those seen loading.</summary>
    private static List<Window> LiveWindows()
    {
        var windows = new List<Window>();
        void Add(object? value)
        {
            if (value is Window window && !windows.Contains(window))
            {
                windows.Add(window);
            }
        }
        lock (KnownWindows)
        {
            foreach (var weak in KnownWindows)
            {
                Add(weak.TryGetTarget(out var w) ? w : null);
            }
        }
        try
        {
            if (Application.Current is { } app)
            {
                for (var type = app.GetType(); type is not null && type != typeof(Application); type = type.BaseType)
                {
                    foreach (var field in type.GetFields(InstanceMembers | BindingFlags.DeclaredOnly))
                    {
                        Add(field.GetValue(app));
                    }
                }
            }
        }
        catch (COMException)
        {
            // The application object isn't created yet.
        }
        if (Assembly.GetEntryAssembly() is { } entry)
        {
            Type[] types;
            try
            {
                types = entry.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }
            foreach (var field in types.SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(f => typeof(Window).IsAssignableFrom(f.FieldType) && !f.FieldType.ContainsGenericParameters && !f.DeclaringType!.ContainsGenericParameters))
            {
                Add(field.GetValue(null));
            }
        }
        return windows;
    }

    /// <summary>Runs on the UI thread after an edit-and-continue delta (if any) was applied.</summary>
    public static void Apply(HotReloadRequest request, List<string> messages, List<string> errors)
    {
        s_messages = messages;
        s_errors = errors;
        try
        {
            var everything = false;
            if (request.Assemblies.Count > 0)
            {
                ReflectionMetadataProvider.AddAssemblies(request.Assemblies);
                s_service?.InvalidateTypeCaches();
                // A library with default styles: controls pick them up as they are re-created.
                everything |= request.Assemblies.Aggregate(false, (any, name) => AddLibraryMarkup(name) | any);
            }
            foreach (var markup in request.Markup)
            {
                ClearErrorInfo();
                LineMaps[markup.Uri] = markup.LineMap;
                if (markup.Kind == MarkupKind.Component && markup.Manifest.ClassName.Length > 0)
                {
                    ClassUris[markup.Manifest.ClassName] = markup.Uri;
                }
                if (markup.Kind is MarkupKind.Application or MarkupKind.Generic)
                {
                    try
                    {
                        // App.xaml's resources are swapped in whole. A Generic.xaml is only checked here: the runtime
                        // reads it through the override when it looks up a default style.
                        var resources = (ResourceDictionary)XamlReader.Load(markup.Xaml);
                        if (markup.Kind == MarkupKind.Application)
                        {
                            KeepMissing(Application.Current.Resources, resources);
                            Application.Current.Resources = resources;
                            everything = true;
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is COMException or XamlParseException or InvalidCastException)
                    {
                        ReportError(Describe(ex, markup.Uri));
                        continue;
                    }
                }
                if (markup.Kind == MarkupKind.Component)
                {
                    Manifests[markup.Uri] = markup.Manifest;
                    foreach (var stale in Hooks.Keys.Where(k => k.StartsWith(markup.Manifest.ClassName + "#", StringComparison.Ordinal)).ToList())
                    {
                        Hooks.Remove(stale);
                    }
                    foreach (var (id, hook) in markup.Manifest.Hooks)
                    {
                        Hooks[id] = hook;
                    }
                }
                var hr = s_service!.SetComponentMarkup(markup.Uri, binary: false, Encoding.UTF8.GetBytes(markup.Xaml));
                if (hr >= 0 && markup.Uri.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                {
                    // The text wins over a compiled XBF served for the same file (a library's, from its .pri).
                    _ = s_service.RemoveComponentMarkup(markup.Uri[..^".xaml".Length] + ".xbf");
                }
                if (hr < 0)
                {
                    ReportError($"{markup.Uri}: {Marshal.GetExceptionForHR(hr)?.Message}");
                    continue;
                }
                everything |= markup.Kind == MarkupKind.Generic;
                if (markup.Kind == MarkupKind.Dictionary)
                {
                    try
                    {
                        ReplaceDictionaries(Application.Current.Resources, new Uri(markup.Uri));
                    }
                    catch (Exception ex) when (ex is COMException or XamlParseException)
                    {
                        ReportError(Describe(ex, markup.Uri));
                    }
                    everything = true;
                }
            }
            if (request.Deltas.Count > 0)
            {
                s_service?.InvalidateTypeCaches();
            }
            try
            {
                Refresh(request.RefreshTypes.ToHashSet(StringComparer.Ordinal), request.RecreateTypes.ToHashSet(StringComparer.Ordinal), request.RefreshAllPages, everything);
            }
            catch (Exception ex) when (ex is COMException or XamlParseException or InvalidOperationException or ArgumentException)
            {
                // The code is in; the UI didn't rebuild. The markup is sent again with the next save.
                ReportError("the UI could not be rebuilt: " + ex.Message);
            }
            NotifyChangedProperties(request.ChangedProperties, request.RefreshBindings);
            if (request.Deltas.Count > 0)
            {
                ReevaluateFunctions();
            }
            if (request.Assets.Count > 0)
            {
                ChangedAssets.UnionWith(request.Assets);
                foreach (var window in LiveWindows())
                {
                    ReloadImages(window.Content, replaced: true);
                }
            }
        }
        finally
        {
            s_messages = null;
            s_errors = null;
        }
    }

    /// <summary>
    /// A library deployed while the app runs has its compiled markup embedded in its .pri, which only a build merges
    /// into the app's resources. Its XBFs are served from there instead. Returns whether it has a Generic.xaml.
    /// </summary>
    private static bool AddLibraryMarkup(string assemblyName)
    {
        var pri = Path.Combine(AppContext.BaseDirectory, assemblyName + ".pri");
        if (s_service is null || !File.Exists(pri))
        {
            return false;
        }
        var generic = false;
        var map = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(pri).MainResourceMap;
        for (uint i = 0; i < map.ResourceCount; i++)
        {
            var (key, candidate) = map.GetValueByIndex(i);
            if (!key.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase) || candidate.Kind != Microsoft.Windows.ApplicationModel.Resources.ResourceCandidateKind.EmbeddedData)
            {
                continue;
            }
            // "Files/CommunityToolkit.WinUI.Controls.SettingsControls/Themes/Generic.xbf"
            var relative = key.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) ? key["Files/".Length..] : key;
            if (!relative.StartsWith(assemblyName + "/", StringComparison.OrdinalIgnoreCase))
            {
                relative = assemblyName + "/" + relative;
            }
            var xbf = candidate.ValueAsBytes;
            var xbfUri = "ms-appx:///" + relative;
            var xamlUri = xbfUri[..^".xbf".Length] + ".xaml";
            foreach (var uri in new[] { xbfUri, xamlUri })
            {
                _ = s_service.SetComponentMarkup(uri, binary: true, xbf);
            }
            HotReloadAgent.Log($"library markup {xamlUri}");
            generic |= relative.EndsWith("/Themes/Generic.xbf", StringComparison.OrdinalIgnoreCase);
        }
        return generic;
    }

    /// <summary>Runtime markup positions to the user's file, by markup URI (see MarkupPayload.LineMap).</summary>
    private static readonly Dictionary<string, List<int[]>> LineMaps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ClassUris = new(StringComparer.Ordinal);

    /// <summary>What .NET puts first when the system has no text for the parser's HRESULT; the parser's own text follows.</summary>
    private const string NoText = "The text associated with this error code could not be found.";

    [DllImport("oleaut32.dll")]
    private static extern int SetErrorInfo(uint reserved, IntPtr errorInfo);

    [DllImport("api-ms-win-core-winrt-error-l1-1-1.dll")]
    private static extern int SetRestrictedErrorInfo(IntPtr errorInfo);

    /// <summary>
    /// Forgets error details a previous failure left on the thread, so a failure that comes without its own isn't
    /// reported with an earlier one's message.
    /// </summary>
    private static void ClearErrorInfo()
    {
        try
        {
            _ = SetErrorInfo(0, IntPtr.Zero);
            _ = SetRestrictedErrorInfo(IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// "Pages/Home.xaml(13,40): Failed to create a 'Brush' from the text 'Oranj'." for a parse error in the markup
    /// at <paramref name="uri"/>, with the runtime's position mapped back to the user's file.
    /// </summary>
    internal static string Describe(Exception ex, string? uri, string? component = null)
    {
        var message = string.Join(" ", ex.GetBaseException().Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != NoText));
        var position = System.Text.RegularExpressions.Regex.Match(message, @"\s*\[Line: (\d+) Position: (\d+)\]");
        if (uri is null)
        {
            return component is null ? message : $"{component}: {message}";
        }
        if (!position.Success)
        {
            return $"{uri}: {message}";
        }
        var (line, column) = (int.Parse(position.Groups[1].Value), int.Parse(position.Groups[2].Value));
        if (LineMaps.TryGetValue(uri, out var map) && map.LastOrDefault(m => m[0] < line || (m[0] == line && m[1] <= column)) is { } at)
        {
            // Same line as the element or attribute it falls in: keep the offset; otherwise its start.
            (line, column) = at[0] == line ? (at[2], at[3] + column - at[1]) : (at[2], at[3]);
        }
        return $"{uri}({line},{column}): {message.Remove(position.Index, position.Length)}";
    }

    /// <summary>A reload that didn't take effect: the save is reported as an error and retried with the next one.</summary>
    private static void ReportError(string message)
    {
        if (s_errors is { } errors && !errors.Contains(message))
        {
            errors.Add(message);
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning("Hot reload: {0}", message);
        }
    }

    private static void Report(string message)
    {
        if (s_messages is { } messages)
        {
            if (!messages.Contains(message))
            {
                messages.Add(message);
            }
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning("Hot reload: {0}", message);
        }
    }

    /// <summary>"Styles/Colors.xaml" for ms-appx:///Styles/Colors.xaml and for the compiled ms-resource:///Files/Styles/Colors.xaml.</summary>
    private static string AppRelativePath(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return uri.OriginalString.TrimStart('/');
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        return uri.Scheme == "ms-resource" && path.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) ? path["Files/".Length..] : path;
    }

    /// <summary>Re-loads app-level dictionaries merged from <paramref name="uri"/>; window and page dictionaries reload with them.</summary>
    private static void ReplaceDictionaries(ResourceDictionary dictionary, Uri uri)
    {
        var merged = dictionary.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source is { } source && string.Equals(AppRelativePath(source), AppRelativePath(uri), StringComparison.OrdinalIgnoreCase))
            {
                var updated = new ResourceDictionary { Source = uri };
                KeepMissing(merged[i], updated);
                merged[i] = updated;
            }
            else
            {
                ReplaceDictionaries(merged[i], uri);
            }
        }
    }

    /// <summary>
    /// Keys a dictionary no longer defines stay resolvable with their old values until the app restarts, as removed
    /// fields do: a resource renamed where it's defined first doesn't break the markup that still uses the old key.
    /// </summary>
    private static void KeepMissing(ResourceDictionary old, ResourceDictionary updated)
    {
        foreach (var (key, value) in old.ToList())
        {
            if (!updated.ContainsKey(key))
            {
                try
                {
                    updated[key] = value;
                }
                catch (Exception ex) when (ex is COMException or ArgumentException)
                {
                    // A value that can't be shared between dictionaries (an element): the key goes away.
                }
            }
        }
        foreach (var (theme, oldTheme) in old.ThemeDictionaries.ToList())
        {
            if (oldTheme is ResourceDictionary oldDictionary && updated.ThemeDictionaries.TryGetValue(theme, out var newTheme) && newTheme is ResourceDictionary newDictionary)
            {
                KeepMissing(oldDictionary, newDictionary);
            }
        }
    }

    /// <summary>
    /// Rebuilds the live UI after an update. Instances are kept and re-initialized in place (constructor body replayed),
    /// so pages, their view models and fields survive. Pages are re-created through their Frame only when how they are
    /// constructed changed (<paramref name="recreate"/>, <paramref name="allPages"/>). <paramref name="everything"/> is
    /// for resource changes: every window is rebuilt and every page reloads its markup.
    /// </summary>
    private static void Refresh(HashSet<string> replay, HashSet<string> recreate, bool allPages, bool everything)
    {
        if (replay.Count == 0 && recreate.Count == 0 && !allPages && !everything)
        {
            return;
        }
        bool Affected(object component) => replay.Contains(component.GetType().FullName!) || recreate.Contains(component.GetType().FullName!);
        MarkChanged(replay.Concat(recreate), allPages || everything);
        s_loadedDuringRefresh = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            // Pages moved into a re-initialized window: its new tree isn't laid out yet, so a walk can't reach them.
            var kept = new List<(Frame Frame, Page Page)>();
            foreach (var window in LiveWindows())
            {
                if (everything || Affected(window))
                {
                    var state = UiState.Take(window.Content);
                    var frames = NamedFrames(window.Content);
                    var selections = NavigationSelections(window.Content);
                    var oldRoot = window.Content;
                    Reinitialize(window);
                    var keptHere = KeepPages(window, oldRoot, window.Content as FrameworkElement, frames);
                    kept.AddRange(keptHere);
                    KeepLocation(window.Content as FrameworkElement, keptHere.Select(k => k.Frame).ToList(), selections);
                    UiState.RestoreWhenLoaded(window.Content as FrameworkElement, state, s_dispatcher);
                }
            }
            foreach (var window in LiveWindows())
            {
                var frames = new List<(Frame Frame, Page Page)>();
                var others = new List<FrameworkElement>();
                Collect(window.Content, page => allPages || everything || Affected(page), Affected, frames, others);
                frames.AddRange(kept.Where(k => (allPages || everything || Affected(k.Page)) && !s_loadedDuringRefresh.Contains(k.Page) && !frames.Any(f => ReferenceEquals(f.Page, k.Page))));
                kept.Clear();
                foreach (var (frame, page) in frames)
                {
                    var state = UiState.Take(page.Content);
                    var type = page.GetType().FullName!;
                    if (allPages || recreate.Contains(type))
                    {
                        Renavigate(frame, page);
                    }
                    else if (replay.Contains(type))
                    {
                        Reinitialize(page);
                    }
                    else
                    {
                        // Only resources changed: reload the markup, without replaying the constructor.
                        ReloadMarkup(page);
                    }
                    UiState.RestoreWhenLoaded((frame.Content as Page)?.Content as FrameworkElement, state, s_dispatcher);
                }
                foreach (var element in others)
                {
                    var state = UiState.Take(ContentRoot(element));
                    Reinitialize(element);
                    UiState.RestoreWhenLoaded(ContentRoot(element) as FrameworkElement, state, s_dispatcher);
                }
            }
        }
        finally
        {
            s_loadedDuringRefresh = null;
        }
    }
    /// <summary>
    /// A property getter, converter or x:Bind function whose body changed computes a new value, but nothing tells
    /// bindings to read it again. Raise PropertyChanged for changed getters on live instances, then have every live
    /// binding read its source again: compiled x:Bind (page scope and template items) and markup bindings, which
    /// covers converters and objects that never raise PropertyChanged.
    /// </summary>
    private static void NotifyChangedProperties(List<string> properties, bool refresh)
    {
        if (properties.Count == 0 && !refresh)
        {
            return;
        }
        var byType = properties.Select(p => p.Split('|')).GroupBy(p => p[0], p => p[1], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var components = new List<object>();
        var roots = new List<object>();
        var elements = new List<FrameworkElement>();
        foreach (var window in LiveWindows())
        {
            components.Add(window);
            void Walk(DependencyObject node)
            {
                if (node is Page or UserControl)
                {
                    components.Add(node);
                }
                if (node is FrameworkElement { DataContext: { } context })
                {
                    roots.Add(context);
                }
                if (refresh && node is FrameworkElement element)
                {
                    elements.Add(element);
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    Walk(VisualTreeHelper.GetChild(node, i));
                }
            }
            if (window.Content is { } content)
            {
                Walk(content);
            }
        }
        foreach (var instance in LiveObjects.Reachable(components.Concat(roots)))
        {
            for (var type = instance.GetType(); type is not null; type = type.BaseType)
            {
                if (type.FullName is { } name && byType.TryGetValue(name, out var names))
                {
                    foreach (var property in names)
                    {
                        RaisePropertyChanged(instance, property);
                    }
                }
            }
        }
        foreach (var component in components)
        {
            try
            {
                // The compiled x:Bind object, or the stand-in live reload installed.
                var bindings = component.GetType().GetField("Bindings", InstanceMembers)?.GetValue(component);
                bindings?.GetType().GetMethod("Update")?.Invoke(bindings, null);
            }
            catch (Exception ex)
            {
                Report($"{component.GetType().Name}: re-evaluating x:Bind failed: {ex.GetBaseException().Message}");
            }
        }
        foreach (var element in elements)
        {
            try
            {
                // An item of a compiled DataTemplate: its x:Bind runs again against the item, phase by phase (x:Phase).
                if (Microsoft.UI.Xaml.Markup.XamlBindingHelper.GetDataTemplateComponent(element) is { } template)
                {
                    // ItemsRepeater hands the item to the template without setting DataContext; the compiled
                    // template keeps it as its data root.
                    var item = element.DataContext ?? template.GetType().GetField("dataRoot", InstanceMembers)?.GetValue(template);
                    for (int phase = 0, guard = 0; item is not null && phase >= 0 && guard < 16; guard++)
                    {
                        template.ProcessBindings(item, 0, phase, out var next);
                        phase = next;
                    }
                }
                foreach (var property in DependencyProperties(element.GetType()))
                {
                    if (element.GetBindingExpression(property) is { ParentBinding: { } binding })
                    {
                        element.SetBinding(property, binding);
                    }
                }
            }
            catch (Exception ex)
            {
                HotReloadAgent.Log($"refreshing bindings on {element.GetType().Name} failed: {ex.Message}");
            }
        }
    }

    private static readonly Dictionary<Type, DependencyProperty[]> s_dependencyProperties = [];

    /// <summary>The dependency properties a type and its bases declare (TextBlock.TextProperty, ...).</summary>
    private static DependencyProperty[] DependencyProperties(Type type)
    {
        if (!s_dependencyProperties.TryGetValue(type, out var properties))
        {
            var found = new List<DependencyProperty>();
            for (var t = type; t is not null && t != typeof(DependencyObject); t = t.BaseType)
            {
                foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(p => p.PropertyType == typeof(DependencyProperty)))
                {
                    try
                    {
                        if (property.GetValue(null) is DependencyProperty value)
                        {
                            found.Add(value);
                        }
                    }
                    catch (Exception ex) when (ex is TargetInvocationException or COMException)
                    {
                        // Not available on this Windows version.
                    }
                }
            }
            s_dependencyProperties[type] = properties = [.. found];
        }
        return properties;
    }

    private static void RaisePropertyChanged(object instance, string property)
    {
        if (instance is not System.ComponentModel.INotifyPropertyChanged)
        {
            return;
        }
        var handler = LiveObjects.Fields(instance.GetType()).FirstOrDefault(f => f.Name == "PropertyChanged" && typeof(System.ComponentModel.PropertyChangedEventHandler).IsAssignableFrom(f.FieldType))
            ?.GetValue(instance) as System.ComponentModel.PropertyChangedEventHandler;
        try
        {
            handler?.Invoke(instance, new System.ComponentModel.PropertyChangedEventArgs(property));
        }
        catch (Exception ex)
        {
            Report($"{instance.GetType().Name}.{property}: a PropertyChanged handler failed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Frames by x:Name with the page they show, so a re-initialized window keeps its pages (and their view models).</summary>
    private static Dictionary<string, Frame> NamedFrames(DependencyObject? root)
    {
        var frames = new Dictionary<string, Frame>(StringComparer.Ordinal);
        void Walk(DependencyObject node)
        {
            if (node is Frame { Content: Page } frame && !string.IsNullOrEmpty(frame.Name))
            {
                frames.TryAdd(frame.Name, frame);
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
        if (root is not null)
        {
            Walk(root);
        }
        return frames;
    }

    private static List<(Frame Frame, Page Page)> KeepPages(object component, DependencyObject? oldRoot, FrameworkElement? root, Dictionary<string, Frame> oldFrames)
    {
        var kept = new List<(Frame, Page)>();
        foreach (var (name, oldFrame) in oldFrames)
        {
            if (root?.FindName(name) is not Frame frame || ReferenceEquals(frame, oldFrame) || oldFrame.Content is not Page page)
            {
                continue;
            }
            if (KeepFrame(component, oldRoot, root, frame, oldFrame))
            {
                // The frame itself stays: its page, back and forward stacks and page cache are as the user left them.
                kept.Add((oldFrame, page));
                continue;
            }
            var backStack = oldFrame.BackStack.ToList();
            oldFrame.Content = null;
            frame.Content = page;
            // Navigation sets Page.Frame; moving the page doesn't, and the page's own Frame.Navigate/GoBack need it.
            page.SetValue(Page.FrameProperty, frame);
            kept.Add((frame, page));
            void CopyBackStack()
            {
                frame.BackStack.Clear();
                // An entry belongs to its frame; the new frame gets copies.
                backStack.ForEach(e => frame.BackStack.Add(new PageStackEntry(e.SourcePageType, e.Parameter, e.NavigationTransitionInfo)));
            }
            try
            {
                CopyBackStack();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // The new frame is still navigating (the window's constructor navigated it): copy once that settles.
                s_dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        CopyBackStack();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    {
                        Report($"{name}: the back stack was not kept: {ex.Message}");
                    }
                });
            }
        }
        return kept;
    }

    /// <summary>What each NavigationView (by x:Name, else by order) had selected: an item's Tag, else its Content.</summary>
    private static List<(string View, string Item)> NavigationSelections(DependencyObject? root)
    {
        var result = new List<(string, string)>();
        var index = 0;
        foreach (var view in Descendants(root).OfType<NavigationView>())
        {
            var key = string.IsNullOrEmpty(view.Name) ? $"#{index}" : view.Name;
            index++;
            if (ReferenceEquals(view.SelectedItem, view.SettingsItem) && view.SelectedItem is not null)
            {
                result.Add((key, "\u0001settings"));
            }
            else if (ItemKey(view.SelectedItem) is { } item)
            {
                result.Add((key, item));
            }
        }
        return result;
    }

    private static string? ItemKey(object? item) => item switch
    {
        null => null,
        NavigationViewItem navigationItem => navigationItem.Tag?.ToString() ?? navigationItem.Content?.ToString(),
        _ => item.ToString(),
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null)
        {
            yield break;
        }
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            // Pages kept from the old window have their own navigation, which isn't being rebuilt.
            if (node is Page && !ReferenceEquals(node, root))
            {
                continue;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>
    /// A rebuilt window shows the page the user was on: its markup re-selects the default navigation item (and the
    /// app's SelectionChanged handler navigates), so navigation on the kept frames is held off until the new tree has
    /// settled, and each NavigationView's selection is put back.
    /// </summary>
    private static void KeepLocation(FrameworkElement? root, List<Frame> frames, List<(string View, string Item)> selections)
    {
        if (root is null || (frames.Count == 0 && selections.Count == 0) || s_dispatcher is not { } dispatcher)
        {
            return;
        }
        void Cancel(object sender, Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e) => e.Cancel = true;
        frames.ForEach(frame => frame.Navigating += Cancel);
        void Release() => frames.ForEach(frame => frame.Navigating -= Cancel);
        void Settle()
        {
            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    RestoreSelections(root, selections);
                }
                finally
                {
                    // The selection change handlers run before this.
                    dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Release);
                }
            });
        }
        if (root.IsLoaded)
        {
            Settle();
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            Settle();
        }
        root.Loaded += OnLoaded;
    }

    private static void RestoreSelections(DependencyObject root, List<(string View, string Item)> selections)
    {
        var index = 0;
        foreach (var view in Descendants(root).OfType<NavigationView>().ToList())
        {
            var key = string.IsNullOrEmpty(view.Name) ? $"#{index}" : view.Name;
            index++;
            if (selections.FirstOrDefault(s => s.View == key).Item is not { } wanted)
            {
                continue;
            }
            if (wanted == "\u0001settings")
            {
                view.SelectedItem = view.SettingsItem;
                continue;
            }
            IEnumerable<object> All(IEnumerable<object> items) => items.SelectMany(i => i is NavigationViewItem n ? All(n.MenuItems).Prepend(i) : [i]);
            if (All(view.MenuItems.Concat(view.FooterMenuItems)).FirstOrDefault(i => ItemKey(i) == wanted) is { } match && !ReferenceEquals(view.SelectedItem, match))
            {
                view.SelectedItem = match;
            }
        }
    }

    /// <summary>Layout the new markup gives the frame, carried onto the frame that is kept.</summary>
    private static readonly DependencyProperty[] FrameLayout =
    [
        Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty, FrameworkElement.MarginProperty,
        FrameworkElement.HorizontalAlignmentProperty, FrameworkElement.VerticalAlignmentProperty, FrameworkElement.WidthProperty,
        FrameworkElement.HeightProperty, FrameworkElement.MinWidthProperty, FrameworkElement.MinHeightProperty, Control.PaddingProperty,
        Control.BackgroundProperty, UIElement.VisibilityProperty, Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty,
    ];

    /// <summary>Puts <paramref name="oldFrame"/> where the rebuilt markup put <paramref name="newFrame"/>; false when it can't.</summary>
    private static bool KeepFrame(object component, DependencyObject? oldRoot, DependencyObject? newRoot, Frame newFrame, Frame oldFrame)
    {
        // The new tree isn't loaded, so parents are found through the content properties markup sets.
        var place = Owner(newRoot, newFrame);
        if (place is null)
        {
            HotReloadAgent.Log($"keeping frame {oldFrame.Name}: no known parent");
            return false;
        }
        try
        {
            Owner(oldRoot, oldFrame)?.Invoke(null);
            foreach (var property in FrameLayout)
            {
                var value = newFrame.ReadLocalValue(property);
                if (value == DependencyProperty.UnsetValue)
                {
                    oldFrame.ClearValue(property);
                }
                else
                {
                    oldFrame.SetValue(property, value);
                }
            }
            place(oldFrame);
            // The component's x:Name field now names the frame that is in the tree.
            foreach (var field in component.GetType().GetFields(InstanceMembers).Where(f => f.FieldType.IsInstanceOfType(oldFrame)))
            {
                if (ReferenceEquals(field.GetValue(component), newFrame))
                {
                    field.SetValue(component, oldFrame);
                }
            }
            HotReloadAgent.Log($"kept frame {oldFrame.Name}");
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            HotReloadAgent.Log($"keeping frame {oldFrame.Name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sets what holds <paramref name="target"/> under <paramref name="node"/> (null removes it), or null when not found.</summary>
    private static Action<UIElement?>? Owner(DependencyObject? node, UIElement target)
    {
        switch (node)
        {
            case null:
                return null;
            case Panel panel:
                for (var i = 0; i < panel.Children.Count; i++)
                {
                    var index = i;
                    if (ReferenceEquals(panel.Children[i], target))
                    {
                        return element => { if (element is null) { panel.Children.RemoveAt(index); } else { panel.Children[index] = element; } };
                    }
                    if (Owner(panel.Children[i], target) is { } inner)
                    {
                        return inner;
                    }
                }
                return null;
            case Border border:
                return ReferenceEquals(border.Child, target) ? element => border.Child = element : Owner(border.Child, target);
            case Viewbox box:
                return ReferenceEquals(box.Child, target) ? element => box.Child = element : Owner(box.Child, target);
            case SplitView split:
                return ReferenceEquals(split.Content, target) ? element => split.Content = element
                    : ReferenceEquals(split.Pane, target) ? element => split.Pane = element
                    : Owner(split.Content, target) ?? Owner(split.Pane, target);
            case UserControl control when control is not Page:
                return ReferenceEquals(control.Content, target) ? element => control.Content = element : Owner(control.Content, target);
            case ContentControl control:
                return ReferenceEquals(control.Content, target) ? element => control.Content = element : Owner(control.Content as DependencyObject, target);
            default:
                return null;
        }
    }
    private static DependencyObject? ContentRoot(object component) => component switch
    {
        Window window => window.Content,
        UserControl control => control.Content,
        ContentControl control => control.Content as DependencyObject,
        _ => component as DependencyObject,
    };

    private static void Collect(DependencyObject? node, Func<Page, bool> pageAffected, Func<object, bool> affected, List<(Frame, Page)> frames, List<FrameworkElement> others)
    {
        if (node is null || s_loadedDuringRefresh!.Contains(node))
        {
            return;
        }
        if (node is Frame { Content: Page page } frame && pageAffected(page) && !s_loadedDuringRefresh.Contains(page))
        {
            frames.Add((frame, page));
            return;
        }
        if (node is FrameworkElement element && node is not Page && affected(element))
        {
            others.Add(element);
            return;
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), pageAffected, affected, frames, others);
        }
    }

    /// <summary>
    /// Re-creates the frame's current page with its original navigation parameter: step to a placeholder so the
    /// current entry lands on the back stack, then go back to it. The frame's stacks end up as they were.
    /// </summary>
    private static void Renavigate(Frame frame, Page page)
    {
        HotReloadAgent.Log($"renavigate {page.GetType().FullName}");
        try
        {
            ClearErrorInfo();
            if (frame.CurrentSourcePageType != page.GetType())
            {
                // Shown without navigating (kept from a rebuilt window): there is no entry to go back to, so the new
                // instance takes its place directly.
                var fresh = (Page)Activator.CreateInstance(page.GetType())!;
                frame.Content = fresh;
                fresh.SetValue(Page.FrameProperty, frame);
                return;
            }
            var forward = frame.ForwardStack.ToList();
            if (!frame.Navigate(typeof(Page), null, new SuppressNavigationTransitionInfo()))
            {
                return;
            }
            frame.GoBack(new SuppressNavigationTransitionInfo());
            frame.ForwardStack.Clear();
            foreach (var entry in forward)
            {
                frame.ForwardStack.Add(entry);
            }
        }
        catch (Exception ex)
        {
            // Keep the page the user was looking at rather than a half-built one or the placeholder.
            if (frame.Content?.GetType() != page.GetType())
            {
                frame.Content = page;
            }
            ReportError(Describe(ex, ClassUris.GetValueOrDefault(page.GetType().FullName!), page.GetType().Name));
        }
    }

    /// <summary>Lets InitializeComponent run again on the same instance: markup re-adds its own resources.</summary>
    private static void PrepareForLoad(object component)
    {
        for (var t = component.GetType(); t is not null; t = t.BaseType)
        {
            t.GetField("_contentLoaded", InstanceMembers | BindingFlags.DeclaredOnly)?.SetValue(component, false);
        }
        if (component is FrameworkElement element)
        {
            element.Resources = new ResourceDictionary();
        }
    }

    /// <summary>
    /// What a failed reload puts back: markup that doesn't load (a resource it uses isn't defined yet) must leave the
    /// UI the user had, not a half-built tree.
    /// </summary>
    private static Action Snapshot(object component)
    {
        var resources = (component as FrameworkElement)?.Resources;
        var content = component switch { Window window => window.Content, UserControl control => control.Content, ContentControl control => control.Content, _ => null };
        return () =>
        {
            try
            {
                if (component is FrameworkElement element && resources is not null)
                {
                    element.Resources = resources;
                }
                switch (component)
                {
                    case Window window: window.Content = content as UIElement; break;
                    case UserControl control: control.Content = content as UIElement; break;
                    case ContentControl control: control.Content = content; break;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                HotReloadAgent.Log($"restoring {component.GetType().Name} failed: {ex.Message}");
            }
        };
    }

    private static void ReloadMarkup(object component)
    {
        var restore = Snapshot(component);
        try
        {
            ClearErrorInfo();
            PrepareForLoad(component);
            component.GetType().GetMethod("InitializeComponent", InstanceMembers, Type.EmptyTypes)?.Invoke(component, null);
        }
        catch (Exception ex)
        {
            restore();
            ReportError(Describe(ex, ClassUris.GetValueOrDefault(component.GetType().FullName!), component.GetType().Name));
        }
    }

    private static void Reinitialize(object component)
    {
        var type = component.GetType();
        HotReloadAgent.Log($"reinitialize {type.FullName}");
        var restore = Snapshot(component);
        try
        {
            ClearErrorInfo();
            PrepareForLoad(component);
            var replay = type.GetMethod(ReplayMethod, InstanceMembers, Type.EmptyTypes);
            // The constructor body runs again; subscriptions it makes to objects that outlive it would otherwise double.
            var events = replay is null ? null : new LiveObjects.EventSnapshot(component);
            (replay ?? type.GetMethod("InitializeComponent", InstanceMembers, Type.EmptyTypes))?.Invoke(component, null);
            events?.RemoveDuplicates();
        }
        catch (Exception ex)
        {
            restore();
            ReportError(Describe(ex, ClassUris.GetValueOrDefault(type.FullName!), type.Name));
        }
    }

    /// <summary>After every component load (WinUI's load callback), on the loading thread.</summary>
    private static void OnLoaded(IntPtr componentAbi, string uri)
    {
        try
        {
            var component = WinRT.MarshalInspectable<object>.FromAbi(componentAbi);
            if (component is null)
            {
                return;
            }
            HotReloadAgent.Log($"loaded {component.GetType().FullName} {uri}");
            s_loadedDuringRefresh?.Add(component);
            if (ContentRoot(component) is FrameworkElement root)
            {
                void Baseline(object sender, RoutedEventArgs e)
                {
                    root.Loaded -= Baseline;
                    UiState.CaptureBaseline(root);
                    ReloadImages(root);
                }
                root.Loaded += Baseline;
            }
            if (component is Window window)
            {
                lock (KnownWindows)
                {
                    if (!KnownWindows.Any(w => w.TryGetTarget(out var known) && ReferenceEquals(known, window)))
                    {
                        KnownWindows.Add(new WeakReference<Window>(window));
                    }
                }
            }
            if (Manifests.TryGetValue(uri, out var manifest))
            {
                Wire(component, manifest);
            }
            Stamp(component);
        }
        catch (Exception ex)
        {
            Report("load callback failed: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Counts refreshes; a component loaded before the last refresh that touched its type is out of date.</summary>
    private static int s_refreshes;
    private static int s_allChanged;
    private static readonly Dictionary<string, int> TypeChanged = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<object, StrongBox<int>> Stamps = new();

    private static void MarkChanged(IEnumerable<string> types, bool all)
    {
        s_refreshes++;
        if (all)
        {
            s_allChanged = s_refreshes;
        }
        foreach (var type in types)
        {
            TypeChanged[type] = s_refreshes;
        }
    }

    private static bool IsStale(object component) =>
        Stamps.TryGetValue(component, out var stamp) &&
        stamp.Value < Math.Max(s_allChanged, TypeChanged.GetValueOrDefault(component.GetType().FullName!));

    /// <summary>
    /// Records that <paramref name="component"/> has the current markup. A page a Frame keeps in its cache
    /// (NavigationCacheMode) isn't in the tree when it changes: it's brought up to date as it's shown again.
    /// </summary>
    private static void Stamp(object component)
    {
        if (Stamps.TryGetValue(component, out var stamp))
        {
            stamp.Value = s_refreshes;
            return;
        }
        Stamps.Add(component, new StrongBox<int>(s_refreshes));
        if (component is Page page)
        {
            page.Loaded += (_, _) =>
            {
                if (IsStale(page) && s_dispatcher is { } dispatcher)
                {
                    dispatcher.TryEnqueue(() =>
                    {
                        if (!IsStale(page))
                        {
                            return;
                        }
                        var state = UiState.Take(page.Content);
                        Reinitialize(page);
                        UiState.RestoreWhenLoaded(page.Content as FrameworkElement, state, dispatcher);
                    });
                }
            };
        }
    }

    /// <summary>Loading handlers waiting to evaluate a component's x:Bind, so a reload before it loads can drop them.</summary>
    private static readonly ConditionalWeakTable<object, StrongBox<Windows.Foundation.TypedEventHandler<FrameworkElement, object>>> PendingBinds = new();

    private static void Wire(object component, ComponentManifest manifest)
    {
        var type = component.GetType();
        var scope = component as FrameworkElement ?? (component as Window)?.Content as FrameworkElement;
        // Names the transform gave unnamed elements so they can be found here. Once found they're cleared: an element's
        // Name is also its UI Automation id and what DevTools shows, and these are no one's.
        var generated = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in manifest.Events.Select(e => e.Element).Concat(manifest.Bindings.Select(b => b.Element)).OfType<string>()
            .Where(n => n.StartsWith(GeneratedNamePrefix, StringComparison.Ordinal)).Distinct())
        {
            if (scope?.FindName(name) is FrameworkElement element)
            {
                generated[name] = element;
                try
                {
                    element.Name = string.Empty;
                }
                catch (COMException)
                {
                }
            }
        }
        object? Find(string? name) => name is null ? component : generated.TryGetValue(name, out var found) ? found : scope?.FindName(name);

        foreach (var name in manifest.Names)
        {
            var field = type.GetField(name, InstanceMembers);
            var value = Find(name);
            if (field is not null && value is not null && field.FieldType.IsInstanceOfType(value))
            {
                field.SetValue(component, value);
            }
        }

        foreach (var (name, text) in manifest.RootProperties)
        {
            try
            {
                if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is { CanWrite: true } property)
                {
                    property.SetValue(component, property.PropertyType == typeof(string) ? text : XamlBindingHelper.ConvertValue(property.PropertyType, text));
                }
            }
            catch (Exception ex)
            {
                Report($"{type.Name}.{name}=\"{text}\": {ex.GetBaseException().Message}");
            }
        }

        foreach (var wiring in manifest.Events)
        {
            var element = Find(wiring.Element);
            var eventInfo = element?.GetType().GetEvent(wiring.Event);
            if (element is null || eventInfo?.EventHandlerType is not { } handlerType)
            {
                Report($"{type.Name}: {wiring.Label ?? wiring.Element ?? "root"} has no event {wiring.Event}");
                continue;
            }
            var handler = MakeHandler(handlerType, wiring.Path is null ? component : ResolvePath(component, wiring.Path), wiring.Handler);
            if (handler is null)
            {
                Report(wiring.Waiting(type.Name));
                continue;
            }
            if (wiring.Element is null)
            {
                // The root survives in-place re-initialization; replace the handler wired by the previous load,
                // or the one compiled code connected when the app started.
                var wired = RootHandlers.GetOrCreateValue(component);
                eventInfo.RemoveEventHandler(element, wired.Remove(wiring.Event, out var previous) ? previous : handler);
                wired[wiring.Event] = handler;
            }
            eventInfo.AddEventHandler(element, handler);
        }

        if (manifest.Bindings.Count > 0 && scope is not null)
        {
            void ApplyAll()
            {
                ResetWatchers(component);
                foreach (var binding in manifest.Bindings)
                {
                    ApplyBinding(component, type, Find(binding.Element) as DependencyObject, binding, component);
                }
            }
            // x:Bind evaluates when the component loads, after the constructor has set up what it binds to.
            if (PendingBinds.TryGetValue(component, out var pending))
            {
                // From an earlier load that was replaced before it got that far: it holds that load's elements.
                scope.Loading -= pending.Value;
                PendingBinds.Remove(component);
            }
            if (component is FrameworkElement { IsLoaded: true } or Window { Visible: true })
            {
                // Re-initialized in place: it won't load again, so evaluate once the constructor replay has finished.
                s_dispatcher?.TryEnqueue(ApplyAll);
            }
            else
            {
                void Bind(FrameworkElement sender, object args)
                {
                    scope.Loading -= Bind;
                    PendingBinds.Remove(component);
                    ApplyAll();
                }
                scope.Loading += Bind;
                PendingBinds.Add(component, new StrongBox<Windows.Foundation.TypedEventHandler<FrameworkElement, object>>(Bind));
            }
            BindingsStandIn.Attach(component, ApplyAll);
        }
        else
        {
            BindingsStandIn.Attach(component, () => { });
        }
    }

    /// <summary>Follows a property path ("ViewModel.Child") from an object; an empty path is the object itself.</summary>
    private static object? ResolvePath(object? source, string path)
    {
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            source = source?.GetType().GetProperty(segment, InstanceMembers)?.GetValue(source)
                ?? source?.GetType().GetField(segment, InstanceMembers)?.GetValue(source);
        }
        return source;
    }

    /// <summary>
    /// A handler of <paramref name="handlerType"/> calling <paramref name="name"/> on <paramref name="target"/>: either a method with
    /// a compatible signature, or (as x:Bind event binding allows) one without parameters.
    /// </summary>
    private static Delegate? MakeHandler(Type handlerType, object? target, string name)
    {
        if (target is null)
        {
            return null;
        }
        var methods = target.GetType().GetMethods(InstanceMembers).Where(m => m.Name == name).ToList();
        foreach (var method in methods)
        {
            if (Delegate.CreateDelegate(handlerType, target, method, throwOnBindFailure: false) is { } handler)
            {
                return handler;
            }
        }
        if (methods.FirstOrDefault(m => m.GetParameters().Length == 0) is { } parameterless)
        {
            var parameters = handlerType.GetMethod("Invoke")!.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
            var call = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(target), parameterless);
            return System.Linq.Expressions.Expression.Lambda(handlerType, call, parameters).Compile();
        }
        return null;
    }

    /// <summary>Watchers of one-way x:Bind functions, per element they bind (or component for root scope), replaced on re-evaluation.</summary>
    private static readonly ConditionalWeakTable<object, List<IDisposable>> Watchers = new();

    private static void ResetWatchers(object owner)
    {
        if (Watchers.TryGetValue(owner, out var list))
        {
            list.ForEach(watcher => watcher.Dispose());
            list.Clear();
        }
    }

    private static DependencyProperty? FindProperty(Type? owner, string name)
    {
        for (var t = owner; t is not null; t = t.BaseType)
        {
            var value = t.GetProperty(name + "Property", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? t.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (value is DependencyProperty property)
            {
                return property;
            }
        }
        return null;
    }

    /// <param name="source">What the path is relative to: the component, or a template item.</param>
    /// <param name="functions">The component type holding generated x:Bind function methods.</param>
    /// <param name="watchOwner">Owns one-way function watchers, so they are dropped on the next evaluation.</param>
    private static void ApplyBinding(object? source, Type functions, DependencyObject? target, RootBinding spec, object watchOwner)
    {
        try
        {
            var owner = spec.OwnerType is null ? target?.GetType() : FindType(spec.OwnerType);
            var property = FindProperty(owner, spec.Property);
            if (target is null || property is null)
            {
                Report($"x:Bind {spec.Path}: {spec.Element ?? "root"}.{spec.Property} was not found");
                return;
            }
            if (spec.Function is { } function)
            {
                BindFunction(source, functions, target, property, owner!.GetProperty(spec.Property)?.PropertyType, spec, function, watchOwner);
                return;
            }
            // Binding only walks public properties; x:Bind also reaches fields (x:Name elements) and non-public members.
            // Those leading steps are resolved here and the binding starts at the object they lead to.
            var path = spec.Path;
            while (path.Length > 0 && source is not null)
            {
                var dot = path.IndexOf('.');
                var head = dot < 0 ? path : path[..dot];
                if (head.Contains('[') || source.GetType().GetProperty(head, BindingFlags.Public | BindingFlags.Instance) is not null)
                {
                    break;
                }
                source = source.GetType().GetField(head, InstanceMembers)?.GetValue(source)
                    ?? source.GetType().GetProperty(head, InstanceMembers)?.GetValue(source);
                path = dot < 0 ? "" : path[(dot + 1)..];
            }
            var binding = new Binding
            {
                Path = new PropertyPath(path),
                Source = source,
                Mode = Enum.TryParse<BindingMode>(spec.Mode, out var mode) ? mode : BindingMode.OneTime,
            };
            if (Enum.TryParse<UpdateSourceTrigger>(spec.UpdateSourceTrigger, out var trigger))
            {
                binding.UpdateSourceTrigger = trigger;
            }
            if (spec.Converter is { } converter && Resource(target, converter) is IValueConverter valueConverter)
            {
                binding.Converter = valueConverter;
            }
            else if (binding.Mode != BindingMode.TwoWay && owner?.GetProperty(spec.Property)?.PropertyType == typeof(string))
            {
                // x:Bind shows any value as text (ToString); a Binding would show the boxed WinRT type name.
                binding.Converter = TextConverter.Instance;
            }
            if (spec.ConverterParameter is { } parameter) binding.ConverterParameter = parameter;
            if (spec.FallbackValue is { } fallback) binding.FallbackValue = fallback;
            if (spec.TargetNullValue is { } nullValue) binding.TargetNullValue = nullValue;
            BindingOperations.SetBinding(target, property, binding);
        }
        catch (Exception ex)
        {
            Report($"x:Bind {spec.Path}: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Content files replaced while the app runs (app-relative, "/"-separated).</summary>
    private static readonly HashSet<string> ChangedAssets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Images showing a replaced file load it again. The image cache would otherwise keep serving the old pixels for
    /// the same URI, so these (and later loads of them, see OnLoaded) skip it.
    /// </summary>
    private static void ReloadImages(DependencyObject? root, bool replaced = false)
    {
        if (root is null || ChangedAssets.Count == 0)
        {
            return;
        }
        BitmapImage? Fresh(ImageSource? source) =>
            source is BitmapImage { UriSource: { } uri } old && ChangedAssets.Any(a => uri.AbsolutePath.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase))
            && (replaced || !old.CreateOptions.HasFlag(BitmapCreateOptions.IgnoreImageCache))
                ? new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelWidth = old.DecodePixelWidth, DecodePixelHeight = old.DecodePixelHeight, UriSource = uri }
                : null;
        Brush? FreshBrush(Brush? brush) =>
            brush is ImageBrush { ImageSource: var source } imageBrush && Fresh(source) is { } image
                ? new ImageBrush { ImageSource = image, Stretch = imageBrush.Stretch, AlignmentX = imageBrush.AlignmentX, AlignmentY = imageBrush.AlignmentY, Opacity = imageBrush.Opacity }
                : null;
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            try
            {
                switch (node)
                {
                    case Image image when Fresh(image.Source) is { } fresh: image.Source = fresh; break;
                    case Microsoft.UI.Xaml.Controls.BitmapIcon icon when icon.UriSource is { } uri && ChangedAssets.Any(a => uri.AbsolutePath.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)):
                        icon.UriSource = null;
                        icon.UriSource = uri;
                        break;
                }
                if (node is Microsoft.UI.Xaml.Controls.ImageIcon imageIcon && Fresh(imageIcon.Source) is { } iconImage) imageIcon.Source = iconImage;
                if (node is Border border && FreshBrush(border.Background) is { } borderBrush) border.Background = borderBrush;
                if (node is Panel panel && FreshBrush(panel.Background) is { } panelBrush) panel.Background = panelBrush;
                if (node is Control control && FreshBrush(control.Background) is { } controlBrush) control.Background = controlBrush;
                if (node is Microsoft.UI.Xaml.Shapes.Shape shape && FreshBrush(shape.Fill) is { } shapeBrush) shape.Fill = shapeBrush;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                HotReloadAgent.Log($"reloading an image on {node.GetType().Name} failed: {ex.Message}");
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>The evaluation of each live x:Bind function binding, by target element and property.</summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Action>> FunctionBindings = new();

    /// <summary>Evaluates every live x:Bind function binding again, after code it may call changed.</summary>
    private static void ReevaluateFunctions()
    {
        foreach (var (target, evaluations) in FunctionBindings.ToList())
        {
            foreach (var evaluate in evaluations.Values.ToList())
            {
                try
                {
                    evaluate();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    HotReloadAgent.Log($"re-evaluating an x:Bind function on {target.GetType().Name} failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Evaluates a generated x:Bind function method and, for one-way bindings, again whenever an argument changes.</summary>
    private static void BindFunction(object? source, Type functions, DependencyObject target, DependencyProperty property, Type? propertyType,
        RootBinding spec, string function, object watchOwner)
    {
        var method = functions.GetMethod(function, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(object)]);
        if (method is null || source is null)
        {
            return;
        }
        var evaluating = false;
        void Evaluate()
        {
            object? value;
            try
            {
                value = method.Invoke(null, [source]);
            }
            catch (TargetInvocationException)
            {
                // Like x:Bind: a null along the path leaves the target at its fallback.
                value = spec.FallbackValue;
            }
            if (value == DependencyProperty.UnsetValue)
            {
                return;
            }
            if (propertyType == typeof(string) && value is not null and not string)
            {
                value = value.ToString();
            }
            else if (value is string text && propertyType is not null && propertyType != typeof(string) && propertyType != typeof(object))
            {
                value = XamlBindingHelper.ConvertValue(propertyType, text);
            }
            evaluating = true;
            try
            {
                target.SetValue(property, value);
            }
            finally
            {
                evaluating = false;
            }
        }
        Evaluate();
        // A code change can change what the function returns (its body, or one it calls) without any argument changing.
        FunctionBindings.GetOrCreateValue(target)[property] = Evaluate;
        if (spec.BindBack is { } bindBack && functions.GetMethod(bindBack, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) is { } back)
        {
            // Two-way: changes the user makes go back through the BindBack method.
            var token = target.RegisterPropertyChangedCallback(property, (sender, dp) =>
            {
                if (!evaluating)
                {
                    try
                    {
                        back.Invoke(null, [source, sender.GetValue(dp)]);
                    }
                    catch (TargetInvocationException ex)
                    {
                        Report($"x:Bind BindBack {spec.Path}: {ex.GetBaseException().Message}");
                    }
                }
            });
            Watchers.GetOrCreateValue(watchOwner).Add(new Callback(() => target.UnregisterPropertyChangedCallback(property, token)));
        }
        if (!string.Equals(spec.Mode, "OneTime", StringComparison.OrdinalIgnoreCase) && spec.Watch.Count > 0)
        {
            Watchers.GetOrCreateValue(watchOwner).Add(new PathWatcher(source, spec.Watch, () =>
            {
                if (s_dispatcher is null || s_dispatcher.HasThreadAccess) Evaluate();
                else s_dispatcher.TryEnqueue(Evaluate);
            }));
        }
    }

    private sealed class Callback(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>Hook specs by id, from the latest markup of each component.</summary>
    private static readonly Dictionary<string, HookSpec> Hooks = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, Delegate>> TemplateHandlers = new();

    /// <summary>Wires an element created from a template: its x:Bind functions follow its DataContext, its events go to the owning component.</summary>
    private static void OnHook(DependencyObject element, string id)
    {
        if (!Hooks.TryGetValue(id, out var spec) || element is not FrameworkElement fe)
        {
            return;
        }
        var className = id[..id.LastIndexOf('#')];
        var functions = FindType(className);
        if (spec.Bindings.Count > 0 && functions is not null)
        {
            void Evaluate()
            {
                ResetWatchers(fe);
                foreach (var binding in spec.Bindings)
                {
                    ApplyBinding(fe.DataContext, functions, fe, binding, fe);
                }
            }
            fe.DataContextChanged += (_, _) => Evaluate();
        }
        if (spec.Events.Count > 0)
        {
            fe.Loaded += (_, _) =>
            {
                DependencyObject? node = fe;
                while (node is not null && node.GetType().FullName != className)
                {
                    node = VisualTreeHelper.GetParent(node);
                }
                if (node is null)
                {
                    return;
                }
                var wired = TemplateHandlers.GetOrCreateValue(fe);
                foreach (var wiring in spec.Events)
                {
                    var eventInfo = fe.GetType().GetEvent(wiring.Event);
                    // Event binding paths start at the item (DataContext); plain handlers live on the owning component.
                    var owner = wiring.Path is null ? node : ResolvePath(fe.DataContext, wiring.Path);
                    var handler = eventInfo?.EventHandlerType is { } handlerType ? MakeHandler(handlerType, owner, wiring.Handler) : null;
                    if (eventInfo is null || handler is null)
                    {
                        continue;
                    }
                    if (wired.Remove(wiring.Event, out var previous))
                    {
                        eventInfo.RemoveEventHandler(fe, previous);
                    }
                    eventInfo.AddEventHandler(fe, handler);
                    wired[wiring.Event] = handler;
                }
            };
        }
    }
    private static object? Resource(DependencyObject target, string markup)
    {
        var key = markup.Trim().TrimStart('{').TrimEnd('}').Trim();
        var space = key.IndexOf(' ');
        if (space < 0)
        {
            return null;
        }
        key = key[(space + 1)..].Trim();
        for (var node = target as FrameworkElement; node is not null; node = node.Parent as FrameworkElement)
        {
            if (node.Resources.TryGetValue(key, out var value))
            {
                return value;
            }
        }
        return Application.Current.Resources.TryGetValue(key, out var appValue) ? appValue : null;
    }

    internal static Type? FindType(string name) =>
        Type.GetType(name, throwOnError: false) ??
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, throwOnError: false)).FirstOrDefault(t => t is not null);
}

/// <summary>Shows a bound value as its text, as x:Bind does for text properties.</summary>
internal sealed partial class TextConverter : IValueConverter
{
    public static readonly TextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, string language) => value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, string language) => value;
}
