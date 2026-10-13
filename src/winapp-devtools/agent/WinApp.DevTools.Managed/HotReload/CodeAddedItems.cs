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
    /// component wasn't rebuilt, e.g. the reload was undone).
    /// </summary>
    public static Action<object> Detach(object component)
    {
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
                if (markup is not null)
                {
                    // The replayed constructor may have added its own items again: those were the first ones it added.
                    var again = FromMarkup.TryGetValue(target.Owner, out var known) && known.TryGetValue(collection, out var rebuiltMarkup)
                        ? target.Items.All().Count(item => !rebuiltMarkup.Contains(item))
                        : 0;
                    added = snapshot.Where(item => !markup.Contains(item)).Skip(again).ToList();
                }
                else if (collection != "Children")
                {
                    // Loaded before the agent was there, so what its markup created isn't known: the items the rebuilt
                    // collection has no counterpart for (by name, or content and tag) are the ones code added.
                    var present = target.Items.All().Select(Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    added = snapshot.Where(item => Key(item) is { } key && !present.Contains(key)).ToList();
                }
                else
                {
                    continue;
                }
                foreach (var item in added)
                {
                    try
                    {
                        items.Remove(item);
                        target.Items.Add(item);
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
                    {
                        HotReloadAgent.Log($"keeping an item of {field}.{collection} failed: {ex.Message}");
                    }
                }
            }
        };
    }

    /// <summary>How an item is recognized in a rebuilt collection: its type with its name, or its content and tag.</summary>
    private static string? Key(object item) => item switch
    {
        Microsoft.UI.Xaml.FrameworkElement { Name.Length: > 0 } named => $"{item.GetType().FullName}|#{named.Name}",
        ContentControl control when control.Content is string or null && (control.Content ?? control.Tag) is not null =>
            $"{item.GetType().FullName}|{control.Content}|{control.Tag}",
        _ => null,
    };

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
