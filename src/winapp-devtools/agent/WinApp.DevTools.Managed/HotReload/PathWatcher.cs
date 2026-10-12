// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Reflection;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Calls back when any property along the given paths changes (INotifyPropertyChanged at each step), the way a
/// one-way x:Bind function re-evaluates when one of its arguments changes.
/// </summary>
internal sealed class PathWatcher : IDisposable
{
    private readonly object _root;
    private readonly IReadOnlyList<string> _paths;
    private readonly Action _changed;
    private readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _subscriptions = [];
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
                    _subscriptions.Add((notifier, handler));
                }
                current = current.GetType().GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current);
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
        foreach (var (source, handler) in _subscriptions)
        {
            source.PropertyChanged -= handler;
        }
        _subscriptions.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        Unsubscribe();
    }
}
