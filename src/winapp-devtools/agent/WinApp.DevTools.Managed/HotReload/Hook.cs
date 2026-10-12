// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// The attached member <c>Hook.Id</c> that live-reloaded markup puts on elements inside templates. The runtime parser
/// sets it on every element it creates from the template, which is how the agent reaches those elements to wire
/// event handlers and x:Bind functions the text parser can't express. Served by <see cref="ReflectionMetadataProvider"/>.
/// </summary>
internal static class Hook
{
    public const string TypeName = "WinApp.DevTools.Managed.HotReload.Hook";

    /// <summary>Set by the agent: called with each element the parser creates and the hook id it carries.</summary>
    public static Action<DependencyObject, string>? Attached { get; set; }

    internal sealed partial class XamlType : IXamlType
    {
        public IXamlType? BaseType => null;
        public IXamlMember? ContentProperty => null;
        public string FullName => TypeName;
        public bool IsArray => false;
        public bool IsCollection => false;
        public bool IsConstructible => false;
        public bool IsDictionary => false;
        public bool IsMarkupExtension => false;
        public bool IsBindable => false;
        public IXamlType? ItemType => null;
        public IXamlType? KeyType => null;
        public IXamlType? BoxedType => null;
        public Type UnderlyingType => typeof(object);
        public object ActivateInstance() => throw new NotSupportedException();
        public object CreateFromString(string value) => throw new NotSupportedException();
        public IXamlMember? GetMember(string name) => name == "Id" ? new Member(this) : null;
        public void AddToVector(object instance, object value) => throw new NotSupportedException();
        public void AddToMap(object instance, object key, object value) => throw new NotSupportedException();
        public void RunInitializer() { }
    }

    private sealed partial class Member(IXamlType declaringType) : IXamlMember
    {
        public bool IsAttachable => true;
        public bool IsDependencyProperty => false;
        public bool IsReadOnly => false;
        public string Name => "Id";
        public IXamlType TargetType => new StringType();
        public IXamlType Type => new StringType();
        public object? GetValue(object instance) => null;
        public void SetValue(object instance, object value)
        {
            HotReloadAgent.Log($"hook set {instance?.GetType().Name} {value}");
            if (instance is DependencyObject element && value?.ToString() is { } id)
            {
                Attached?.Invoke(element, id);
            }
        }
    }

    private sealed partial class StringType : IXamlType
    {
        public IXamlType? BaseType => null;
        public IXamlMember? ContentProperty => null;
        public string FullName => "String";
        public bool IsArray => false;
        public bool IsCollection => false;
        public bool IsConstructible => false;
        public bool IsDictionary => false;
        public bool IsMarkupExtension => false;
        public bool IsBindable => false;
        public IXamlType? ItemType => null;
        public IXamlType? KeyType => null;
        public IXamlType? BoxedType => null;
        public Type UnderlyingType => typeof(string);
        public object ActivateInstance() => "";
        public object CreateFromString(string value) => value;
        public IXamlMember? GetMember(string name) => null;
        public void AddToVector(object instance, object value) => throw new NotSupportedException();
        public void AddToMap(object instance, object key, object value) => throw new NotSupportedException();
        public void RunInitializer() { }
    }
}
