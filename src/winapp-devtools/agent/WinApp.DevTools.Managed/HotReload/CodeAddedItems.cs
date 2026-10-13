// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Items code added to a component's named collections (a NavigationView's menu filled in App.OnLaunched, a panel's
/// children added by a click) aren't in its markup, so re-initializing it would drop them. What the markup created is
/// recorded as each component loads; on a re-init the rest is moved into the rebuilt collection.
/// </summary>
internal static class CodeAddedItems
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Per collection owner (a named element): the items its markup created.</summary>
    private static readonly ConditionalWeakTable<object, Dictionary<string, HashSet<object>>> FromMarkup = new();

    /// <summary>Elements a reload kept in place of the ones the new markup created (a frame and its pages): markup's, not code's.</summary>
    private static readonly ConditionalWeakTable<object, object> StandIns = new();

    /// <summary><paramref name="kept"/> took the place of an element the rebuilt markup created.</summary>
    public static void KeptFromMarkup(object kept) => StandIns.AddOrUpdate(kept, kept);

    /// <summary>See HotReloadRequest.LaunchItems; the first counts for a class describe the markup it launched with.</summary>
    private static readonly Dictionary<string, Dictionary<string, int>> Launched = new(StringComparer.Ordinal);

    public static void LaunchItems(Dictionary<string, Dictionary<string, int>> counts)
    {
        foreach (var (type, items) in counts)
        {
            Launched.TryAdd(type, items);
        }
    }

    /// <summary>Right after a component loaded its markup (before its code adds anything).</summary>
    public static void RecordMarkup(object component)
    {
        foreach (var (owner, collection, items) in Collections(component))
        {
            FromMarkup.AddOrUpdate(owner, Merge(FromMarkup.TryGetValue(owner, out var known) ? known : null, collection, items));
        }
    }

    private static Dictionary<string, HashSet<object>> Merge(Dictionary<string, HashSet<object>>? known, string collection, Items items)
    {
        known ??= [];
        known[collection] = new HashSet<object>(items.All(), ReferenceEqualityComparer.Instance);
        return known;
    }

    /// <summary>A collection of any element type (IList&lt;object&gt;, UIElementCollection, ...).</summary>
    private sealed record Items(Func<IEnumerable<object>> All, Action<object> Add, Action<object> Remove)
    {
        public static Items Of<T>(IList<T> list) => new(() => list.Cast<object>().ToList(), item => list.Add((T)item), item => list.Remove((T)item));
    }

    /// <summary>
    /// Before a re-init: returns what moves the code-added items into the rebuilt collections (nothing moves when the
    /// component wasn't rebuilt, e.g. the reload was undone). <paramref name="leaving"/> is told the field and old owner
    /// items are about to leave, which then raises events (a selected item leaving deselects it).
    /// </summary>
    public static Action<object> Detach(object component, Action<string, object> leaving)
    {
        var own = MarkupUri(component);
        var before = NamedCollections(component)
            .Select(c => (c.Field, c.Owner, c.Collection, c.Items, Markup: FromMarkup.TryGetValue(c.Owner, out var known) && known.TryGetValue(c.Collection, out var markup) ? markup : null, Snapshot: c.Items.All().ToList()))
            .Where(c => c.Snapshot.Count > 0).ToList();
        return rebuilt =>
        {
            var collections = NamedCollections(rebuilt).ToDictionary(c => (c.Field, c.Collection), c => (c.Owner, c.Items));
            foreach (var (field, owner, collection, items, markup, snapshot) in before)
            {
                if (!collections.TryGetValue((field, collection), out var target) || ReferenceEquals(target.Owner, owner))
                {
                    continue;
                }
                List<object> added;
                var how = "recorded markup";
                if (markup is not null)
                {
                    // The replayed constructor may have added its own items again: those were the first ones it added.
                    var again = FromMarkup.TryGetValue(target.Owner, out var known) && known.TryGetValue(collection, out var rebuiltMarkup)
                        ? target.Items.All().Count(item => !rebuiltMarkup.Contains(item))
                        : 0;
                    added = snapshot.Where(item => !markup.Contains(item) && !StandIns.TryGetValue(item, out _)).Skip(again).ToList();
                }
                else if (collection != "Children" && Launched.GetValueOrDefault(component.GetType().FullName!)?.GetValueOrDefault($"{field}.{collection}") is { } launchCount)
                {
                    // Loaded before the agent was there, so what its markup created isn't recorded: the markup it
                    // launched with created the first items, and code added the rest (after them, as code appends).
                    var again = FromMarkup.TryGetValue(target.Owner, out var known) && known.TryGetValue(collection, out var rebuiltMarkup)
                        ? target.Items.All().Count(item => !rebuiltMarkup.Contains(item))
                        : 0;
                    added = snapshot.Skip(launchCount).Where(item => !StandIns.TryGetValue(item, out _)).Skip(again).ToList();
                    how = $"{launchCount} in the launch markup";
                }
                else if (collection != "Children")
                {
                    // Not even that is known: an element the old markup created has that markup's base URI; one code
                    // created only has it when its parent passes it on.
                    // The owner came from the same markup (a window's root may have no base URI).
                    var origin = MarkupUri(owner) is { Length: > 0 } ownerUri ? ownerUri : own;
                    var again = target.Items.All().Count(item => !FromMarkupOf(item, MarkupUri(target.Owner) is { Length: > 0 } rebuiltUri ? rebuiltUri : origin));
                    added = snapshot.Where(item => !FromMarkupOf(item, origin) && !StandIns.TryGetValue(item, out _)).Skip(again).ToList();
                    how = "by base URI";
                }
                else
                {
                    continue;
                }
                if (added.Count > 0)
                {
                    leaving(field, owner);
                }
                HotReloadAgent.Log($"{field}.{collection}: {snapshot.Count} items, {added.Count} added by code ({how})");
                foreach (var item in added)
                {
                    try
                    {
                        items.Remove(item);
                        var restore = Retemplate(item);
                        target.Items.Add(item);
                        restore();
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
                    {
                        HotReloadAgent.Log($"keeping an item of {field}.{collection} failed: {ex.Message}");
                    }
                }
            }
        };
    }

    /// <summary>
    /// A NavigationViewItem keeps the template it got in the old menu, and with it a list of child items wired to the old
    /// NavigationView (selecting a child there gives the new one no container). Its children are taken out, its template
    /// applied again, and the returned action puts them back once it shows in the new menu, in a list wired to it.
    /// </summary>
    private static Action Retemplate(object item)
    {
        if (item is not NavigationViewItem { MenuItems.Count: > 0 } navigationItem || ChildList(navigationItem) is null)
        {
            // Never shown with children: nothing is wired yet.
            return () => { };
        }
        var children = navigationItem.MenuItems.ToList();
        var restoreChildren = children.Select(Retemplate).ToList();
        navigationItem.MenuItems.Clear();
        if (navigationItem.Template is { } template)
        {
            navigationItem.Template = null;
            navigationItem.Template = template;
        }
        return () =>
        {
            void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            {
                navigationItem.Loaded -= OnLoaded;
                try
                {
                    children.ForEach(navigationItem.MenuItems.Add);
                    restoreChildren.ForEach(restore => restore());
                    // The new list starts hidden; expanding shows it.
                    if (navigationItem.IsExpanded)
                    {
                        navigationItem.IsExpanded = false;
                        navigationItem.IsExpanded = true;
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
                {
                    HotReloadAgent.Log($"restoring the child items of {navigationItem.Content} failed: {ex.Message}");
                }
            }
            navigationItem.Loaded += OnLoaded;
        };
    }

    /// <summary>The list a NavigationViewItem shows its child items in (not one of a child's).</summary>
    private static ItemsRepeater? ChildList(Microsoft.UI.Xaml.DependencyObject node)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
            if (child is ItemsRepeater { Name: "NavigationViewItemMenuItemsHost" } list)
            {
                return list;
            }
            if (child is not NavigationViewItem && ChildList(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="item"/> was created by the markup with base URI <paramref name="markup"/>.</summary>
    private static bool FromMarkupOf(object item, string? markup) =>
        markup is not null && item is Microsoft.UI.Xaml.FrameworkElement element && element.BaseUri?.ToString() == markup;

    /// <summary>The base URI of what the component's markup built (its root element's).</summary>
    private static string? MarkupUri(object component) => (component switch
    {
        Microsoft.UI.Xaml.Window window => window.Content,
        UserControl control => control.Content,
        _ => component,
    } as Microsoft.UI.Xaml.FrameworkElement)?.BaseUri?.ToString();

    private static IEnumerable<(string Field, object Owner, string Collection, Items Items)> NamedCollections(object component)
    {
        for (var type = component.GetType(); type is not null && type.Assembly == component.GetType().Assembly; type = type.BaseType)
        {
            foreach (var field in type.GetFields(InstanceFields | BindingFlags.DeclaredOnly))
            {
                if (field.GetValue(component) is not { } owner)
                {
                    continue;
                }
                foreach (var (collection, items) in CollectionsOf(owner))
                {
                    yield return (field.Name, owner, collection, items);
                }
            }
        }
    }

    private static IEnumerable<(object Owner, string Collection, Items Items)> Collections(object component) =>
        NamedCollections(component).Select(c => (c.Owner, c.Collection, c.Items));

    /// <summary>The item collections code commonly fills: navigation menus, tabs, unbound item lists and panels.</summary>
    private static IEnumerable<(string Name, Items Items)> CollectionsOf(object owner)
    {
        switch (owner)
        {
            case NavigationView navigation:
                yield return ("MenuItems", Items.Of(navigation.MenuItems));
                yield return ("FooterMenuItems", Items.Of(navigation.FooterMenuItems));
                break;
            case TabView tabs when tabs.TabItemsSource is null:
                yield return ("TabItems", Items.Of(tabs.TabItems));
                break;
            case ItemsControl list when list.ItemsSource is null:
                yield return ("Items", Items.Of(list.Items));
                break;
            case Panel panel:
                yield return ("Children", Items.Of(panel.Children));
                break;
        }
    }
}
