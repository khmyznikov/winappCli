// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// The app's own objects reachable from the live UI through fields: view models, their collections, models. Used to
/// find instances whose behavior an update changed, without touching framework internals.
/// </summary>
internal static class LiveObjects
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Objects reachable from <paramref name="roots"/> by following fields, descending only into app types.</summary>
    public static HashSet<object> Reachable(IEnumerable<object> roots, int depth = 3)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var frontier = roots.Where(r => r is not null).ToList();
        for (var level = 0; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<object>();
            foreach (var item in frontier)
            {
                if (!seen.Add(item) || level == depth || !IsAppType(item.GetType()) && item is not DependencyObject)
                {
                    continue;
                }
                foreach (var field in Fields(item.GetType()))
                {
                    if (field.FieldType.IsValueType || field.FieldType == typeof(string) || typeof(Delegate).IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }
                    object? value;
                    try
                    {
                        value = field.GetValue(item);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    // Other UI elements are reached through the visual tree, not through fields.
                    if (value is not null and not DependencyObject and not Type)
                    {
                        next.Add(value);
                    }
                }
            }
            frontier = next;
        }
        return seen;
    }

    /// <summary>Instance fields along the type hierarchy, stopping at framework types (a DependencyObject's own fields are app-declared).</summary>
    public static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.Namespace?.StartsWith("Microsoft.UI.", StringComparison.Ordinal) == true || t.Namespace?.StartsWith("WinRT", StringComparison.Ordinal) == true)
            {
                yield break;
            }
            foreach (var field in t.GetFields(InstanceFields))
            {
                yield return field;
            }
        }
    }

    private static bool IsAppType(Type type)
    {
        var name = type.Assembly.GetName().Name ?? "";
        return !(name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            name.StartsWith("WinRT", StringComparison.Ordinal) || name == "mscorlib" || name == "netstandard");
    }

    /// <summary>
    /// Field-backed events (for example a view model's PropertyChanged or a collection's CollectionChanged) of objects
    /// reachable from a component, so subscriptions its replayed constructor makes again can be undone.
    /// </summary>
    public sealed class EventSnapshot
    {
        private readonly List<(object Owner, FieldInfo Field, Delegate? Value)> _events = [];

        public EventSnapshot(object component)
        {
            foreach (var owner in Reachable([component]).Where(o => !ReferenceEquals(o, component)))
            {
                foreach (var field in Fields(owner.GetType()).Where(f => typeof(MulticastDelegate).IsAssignableFrom(f.FieldType)))
                {
                    _events.Add((owner, field, field.GetValue(owner) as Delegate));
                }
            }
        }

        /// <summary>Removes handlers added since the snapshot that duplicate one that was already subscribed.</summary>
        public void RemoveDuplicates()
        {
            foreach (var (owner, field, before) in _events)
            {
                if (field.GetValue(owner) is not Delegate after || before is null)
                {
                    continue;
                }
                var existing = before.GetInvocationList().GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
                var seen = new Dictionary<(object?, MethodInfo), int>();
                var result = after;
                foreach (var handler in after.GetInvocationList())
                {
                    var key = Key(handler);
                    seen[key] = seen.GetValueOrDefault(key) + 1;
                    if (existing.TryGetValue(key, out var count) && seen[key] > count)
                    {
                        result = Delegate.Remove(result, handler);
                    }
                }
                if (!ReferenceEquals(result, after))
                {
                    field.SetValue(owner, result);
                }
            }
        }

        /// <summary>A handler's identity: its method and target, or just the method for a lambda's closure (a new one each run).</summary>
        private static (object?, MethodInfo) Key(Delegate handler) =>
            handler.Target is { } target && target.GetType().IsDefined(typeof(CompilerGeneratedAttribute))
                ? (target.GetType(), handler.Method)
                : (handler.Target, handler.Method);
    }
}
