// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Fallback XAML type information for app types added while the app runs (a new Page reached through
/// Frame.Navigate, a converter declared in resources, a new UserControl with properties). The app's compiled provider
/// knows only the types that existed at build time.
/// </summary>
internal sealed partial class ReflectionMetadataProvider : IXamlMetadataProvider
{
    /// <summary>The XAML type information of libraries referenced while the app runs (a package or a project).</summary>
    private static readonly List<IXamlMetadataProvider> Libraries = [];

    /// <summary>Loads assemblies newly deployed next to the app and serves the XAML types they declare.</summary>
    public static void AddAssemblies(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            Assembly assembly;
            try
            {
                assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or FileLoadException)
            {
                continue; // A native library.
            }
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }
            // The XAML compiler generates one public XamlMetaDataProvider per library.
            foreach (var type in types.Where(t => t.Name == "XamlMetaDataProvider" && typeof(IXamlMetadataProvider).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null))
            {
                HotReloadAgent.Log($"library provider {type.FullName}");
                Libraries.Add((IXamlMetadataProvider)Activator.CreateInstance(type)!);
            }
        }
    }

    public IXamlType? GetXamlType(Type type) => Create(type) ?? FromLibraries(p => p.GetXamlType(type));

    private static IXamlType? FromLibraries(Func<IXamlMetadataProvider, IXamlType?> get)
    {
        foreach (var library in Libraries)
        {
            try
            {
                if (get(library) is { } found)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException or System.Runtime.InteropServices.COMException or NotImplementedException)
            {
            }
        }
        return null;
    }

    public IXamlType? GetXamlType(string fullName)
    {
        HotReloadAgent.Log($"GetXamlType({fullName})");
        if (fullName == Hook.TypeName)
        {
            return new Hook.XamlType();
        }
        var found = XamlReload.FindType(fullName);
        // App types by reflection; library types from the library's own provider, else by reflection too (a library
        // without XAML of its own, such as one with only state triggers, ships no provider).
        var served = (found is { } type ? Create(type) : null) ?? FromLibraries(p => p.GetXamlType(fullName)) ??
            (found is { IsClass: true, IsAbstract: false } other && other.GetConstructor(Type.EmptyTypes) is not null && other.Assembly != typeof(Application).Assembly ? new ReflectionXamlType(other) : null);
        HotReloadAgent.Log($"  -> {found?.AssemblyQualifiedName ?? "not found"} ({served?.GetType().Name ?? "none"})");
        return served;
    }

    public XmlnsDefinition[] GetXmlnsDefinitions() => [];

    private static IXamlType? Create(Type type) =>
        type.Assembly == Assembly.GetEntryAssembly() && type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null
            ? new ReflectionXamlType(type)
            : null;

    /// <summary>XAML type information for any CLR type: app types by reflection, everything else from the app's provider.</summary>
    internal static IXamlType? For(Type? type)
    {
        if (type is null)
        {
            return null;
        }
        if (Create(type) is { } created)
        {
            return created;
        }
        try
        {
            if ((Application.Current as IXamlMetadataProvider)?.GetXamlType(type) is { } known)
            {
                return known;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
        }
        return FromLibraries(p => p.GetXamlType(type)) ?? new ValueType(type);
    }

    private sealed partial class ReflectionXamlType(Type type) : IXamlType
    {
        // Members, inherited ones included, come from reflection: the app provider's framework base types throw
        // E_NOTIMPL for member queries, which fails Frame.Navigate. The framework base is named, though, so the runtime
        // finds its own properties (a Style setter for Control.Template) by walking to it.
        public IXamlType? BaseType => type.BaseType is { } baseType && baseType.Assembly == typeof(Application).Assembly ? new FrameworkType(baseType) : null;
        public IXamlMember? ContentProperty =>
            type.GetCustomAttribute<ContentPropertyAttribute>(inherit: true)?.Name is { } name ? GetMember(name) : null;
        public string FullName => type.FullName!;
        public bool IsArray => false;
        public bool IsCollection => false;
        public bool IsConstructible => true;
        public bool IsDictionary => false;
        public bool IsMarkupExtension => false;
        public bool IsBindable => true;
        public IXamlType? ItemType => null;
        public IXamlType? KeyType => null;
        public IXamlType? BoxedType => null;
        public Type UnderlyingType => type;

        public object ActivateInstance()
        {
            HotReloadAgent.Log($"ActivateInstance({type})");
            return Activator.CreateInstance(type)!;
        }

        public object CreateFromString(string value) => throw new NotSupportedException();

        public IXamlMember? GetMember(string name) =>
            type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) is { } property ? new ReflectionXamlMember(property, this) : null;

        public void AddToVector(object instance, object value) => throw new NotSupportedException();
        public void AddToMap(object instance, object key, object value) => throw new NotSupportedException();
        public void RunInitializer() => RuntimeHelpers.RunClassConstructor(type.TypeHandle);
    }

    private sealed partial class ReflectionXamlMember(PropertyInfo property, IXamlType declaringType) : IXamlMember
    {
        public bool IsAttachable => false;
        // App DPs are static fields; the projection exposes framework DPs (Control.TemplateProperty) as static properties.
        public bool IsDependencyProperty =>
            property.DeclaringType!.GetField(property.Name + "Property", BindingFlags.Public | BindingFlags.Static) is not null ||
            property.DeclaringType!.GetProperty(property.Name + "Property", BindingFlags.Public | BindingFlags.Static)?.PropertyType == typeof(DependencyProperty);
        public bool IsReadOnly => !property.CanWrite;
        public string Name => property.Name;
        public IXamlType TargetType => declaringType;
        public IXamlType Type => For(property.PropertyType)!;
        public object? GetValue(object instance) => property.GetValue(instance);
        public void SetValue(object instance, object value) => property.SetValue(instance, value);
    }

    /// <summary>A WinUI type the runtime knows itself: only its name matters.</summary>
    private sealed partial class FrameworkType(Type type) : IXamlType
    {
        public IXamlType? BaseType => null;
        public IXamlMember? ContentProperty => null;
        public string FullName => type.FullName!;
        public bool IsArray => false;
        public bool IsCollection => false;
        public bool IsConstructible => false;
        public bool IsDictionary => false;
        public bool IsMarkupExtension => false;
        public bool IsBindable => false;
        public IXamlType? ItemType => null;
        public IXamlType? KeyType => null;
        public IXamlType? BoxedType => null;
        public Type UnderlyingType => type;
        public object ActivateInstance() => throw new NotSupportedException();
        public object CreateFromString(string value) => throw new NotSupportedException();
        public IXamlMember? GetMember(string name) => null;
        public void AddToVector(object instance, object value) => throw new NotSupportedException();
        public void AddToMap(object instance, object key, object value) => throw new NotSupportedException();
        public void RunInitializer() { }
    }

    /// <summary>Strings, numbers, enums and other values the parser creates from attribute text.</summary>
    private sealed partial class ValueType(Type type) : IXamlType
    {
        public IXamlType? BaseType => null;
        public IXamlMember? ContentProperty => null;
        public string FullName => type == typeof(string) ? "String" : type.FullName!;
        public bool IsArray => false;
        public bool IsCollection => false;
        public bool IsConstructible => false;
        public bool IsDictionary => false;
        public bool IsMarkupExtension => false;
        public bool IsBindable => false;
        public IXamlType? ItemType => null;
        public IXamlType? KeyType => null;
        public IXamlType? BoxedType => null;
        public Type UnderlyingType => type;
        public object ActivateInstance() => throw new NotSupportedException();
        public object CreateFromString(string value) =>
            type == typeof(string) || type == typeof(object) ? value
            : type.IsEnum ? Enum.Parse(type, value, ignoreCase: true)
            : type.IsPrimitive || type == typeof(decimal) ? Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture)
            : XamlBindingHelper.ConvertValue(type, value);
        public IXamlMember? GetMember(string name) => null;
        public void AddToVector(object instance, object value) => throw new NotSupportedException();
        public void AddToMap(object instance, object key, object value) => throw new NotSupportedException();
        public void RunInitializer() { }
    }
}
