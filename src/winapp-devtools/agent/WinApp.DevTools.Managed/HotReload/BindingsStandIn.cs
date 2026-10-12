// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Stands in for the XAML compiler's x:Bind 'Bindings' object, which compiled code only creates from markup
/// connection ids. Text markup has none, so code-behind calling Bindings.Update() would otherwise hit null.
/// Update re-evaluates the component's root-scope bindings; the other members have nothing to do.
/// </summary>
public class BindingsStandIn : DispatchProxy
{
    internal Action? Update { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "Update")
        {
            Update?.Invoke();
        }
        var returnType = targetMethod?.ReturnType;
        return returnType is null || returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
    }

    internal static void Attach(object component, Action update)
    {
        var field = component.GetType().GetField("Bindings", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null || !field.FieldType.IsInterface || field.GetValue(component) is BindingsStandIn { } existing && existing.Update == update)
        {
            return;
        }
        var proxy = typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
            .MakeGenericMethod(field.FieldType, typeof(BindingsStandIn)).Invoke(null, null)!;
        ((BindingsStandIn)proxy).Update = update;
        field.SetValue(component, proxy);
    }
}
