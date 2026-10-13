// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Reflection;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Calls back when any property along the given paths changes (INotifyPropertyChanged, or a dependency property of an
/// element such as an x:Name'd CheckBox's IsChecked), the way a one-way x:Bind function re-evaluates when one of its
/// arguments changes.
/// </summary>
internal sealed class PathWatcher : IDisposable
{
    private readonly object _root;
    private readonly IReadOnlyList<string> _paths;
    private readonly Action _changed;
    private readonly List<Action> _subscriptions = [];
    private bool _disposed;

    public PathWatcher(object root, IReadOnlyList<string> paths, Action changed)
    {
        _root = root;
        _paths = paths;
        _changed = changed;
        Subscribe();
    }

    private void Subscribe()
    {
        foreach (var path in _paths)
        {
            object? current = _root;
            foreach (var rawSegment in path.Split('.'))
            {
                if (current is null)
                {
                    break;
                }
                var segment = rawSegment.Split('[')[0];
                if (current is INotifyPropertyChanged notifier)
                {
                    PropertyChangedEventHandler handler = (_, e) =>
                    {
                        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == segment)
                        {
                            OnChanged();
                        }
                    };
                    notifier.PropertyChanged += handler;
                    _subscriptions.Add(() => notifier.PropertyChanged -= handler);
                }
                else if (current is Microsoft.UI.Xaml.DependencyObject element && DependencyPropertyOf(element.GetType(), segment) is { } property)
                {
                    var token = element.RegisterPropertyChangedCallback(property, (_, _) => OnChanged());
                    _subscriptions.Add(() => element.UnregisterPropertyChangedCallback(property, token));
                }
                const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                // A property, or a field (an x:Name); a nullable's Value is the boxed value itself.
                current = current.GetType().GetProperty(segment, members) is { } getter ? getter.GetValue(current)
                    : current.GetType().GetField(segment, members) is { } field ? field.GetValue(current)
                    : segment == "Value" && current.GetType().IsValueType ? current
                    : null;
            }
        }
    }

    private void OnChanged()
    {
        if (_disposed)
        {
            return;
        }
        // Objects along the path may have been replaced; follow the new ones.
        Unsubscribe();
        Subscribe();
        _changed();
    }

    private void Unsubscribe()
    {
        foreach (var unsubscribe in _subscriptions)
        {
            unsubscribe();
        }
        _subscriptions.Clear();
    }

    /// <summary>A type's dependency property by name (TextBox.Text: TextBox.TextProperty, declared here or on a base).</summary>
    private static Microsoft.UI.Xaml.DependencyProperty? DependencyPropertyOf(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            try
            {
                if (t.GetProperty(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetValue(null) is Microsoft.UI.Xaml.DependencyProperty property)
                {
                    return property;
                }
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        Unsubscribe();
    }
}
