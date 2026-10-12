// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace WinApp.HotReload;

/// <summary>Resolves XAML element names to WinUI/app types through the app's compilation.</summary>
internal sealed class CompilationTypeResolver(Compilation compilation) : IXamlTypeResolver
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly string[] PresentationNamespaces =
    [
        "Microsoft.UI.Xaml", "Microsoft.UI.Xaml.Controls", "Microsoft.UI.Xaml.Controls.Primitives", "Microsoft.UI.Xaml.Media",
        "Microsoft.UI.Xaml.Media.Animation", "Microsoft.UI.Xaml.Media.Imaging", "Microsoft.UI.Xaml.Media.Media3D", "Microsoft.UI.Xaml.Shapes",
        "Microsoft.UI.Xaml.Documents", "Microsoft.UI.Xaml.Input", "Microsoft.UI.Xaml.Automation", "Microsoft.UI.Xaml.Data",
        "Microsoft.UI.Xaml.Navigation", "Microsoft.UI.Xaml.Markup", "Microsoft.UI.Xaml.Controls.AnimatedVisuals",
    ];
    private readonly ConcurrentDictionary<(string, string), INamedTypeSymbol?> _cache = new();

    public string? Resolve(string xmlNamespace, string localName) => Symbol(xmlNamespace, localName)?.ToDisplayString();

    public INamedTypeSymbol? Symbol(string xmlNamespace, string localName) => _cache.GetOrAdd((xmlNamespace, localName), key =>
    {
        var (ns, name) = key;
        // Element syntax for a markup extension (<StaticResource ResourceKey="..." />) names the class without its suffix.
        return Lookup(ns, name) ?? Lookup(ns, name + "Extension");
    });

    private INamedTypeSymbol? Lookup(string ns, string name)
    {
        if (ns == Presentation)
        {
            return PresentationNamespaces.Select(candidate => compilation.GetTypeByMetadataName(candidate + "." + name)).FirstOrDefault(type => type is not null)
                ?? PresentationTypes.Value.GetValueOrDefault(name);
        }
        var clrNamespace = ClrNamespace(ns);
        return clrNamespace is null ? null : compilation.GetTypeByMetadataName(clrNamespace + "." + name);
    }

    private static string? ClrNamespace(string ns) =>
        ns.StartsWith("using:", StringComparison.Ordinal) ? ns["using:".Length..]
        : ns.StartsWith("clr-namespace:", StringComparison.Ordinal) ? ns["clr-namespace:".Length..].Split(';')[0]
        : null;

    /// <summary>
    /// Every public type the presentation namespace can name: Microsoft.UI.Xaml.* plus the Windows structs XAML uses as
    /// elements (Color, Point, FontWeight...), for names the common namespaces miss.
    /// </summary>
    private Lazy<Dictionary<string, INamedTypeSymbol>> PresentationTypes => _presentationTypes ??= new(() =>
    {
        var types = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        void Walk(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol child)
                {
                    Walk(child);
                }
                else if (member is INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } type &&
                    type.ContainingNamespace.ToDisplayString() is var name &&
                    (name.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) || name.StartsWith("Windows.UI", StringComparison.Ordinal) ||
                     name == "Windows.Foundation"))
                {
                    types.TryAdd(type.Name, type);
                }
            }
        }
        foreach (var root in compilation.GlobalNamespace.GetNamespaceMembers().Where(n => n.Name is "Microsoft" or "Windows"))
        {
            foreach (var child in root.GetNamespaceMembers().Where(n => n.Name is "UI" or "Foundation"))
            {
                Walk(child);
            }
        }
        return types;
    });

    private Lazy<Dictionary<string, INamedTypeSymbol>>? _presentationTypes;

    /// <summary>True when the namespace is one whose types this resolver can list completely, so a miss is a real error.</summary>
    public bool IsKnownNamespace(string xmlNamespace) => xmlNamespace == Presentation || ClrNamespace(xmlNamespace) is not null;

    public INamedTypeSymbol? ByName(string fullName) => compilation.GetTypeByMetadataName(fullName);

    public bool HasEvent(string typeName, string member)
    {
        for (var type = ByName(typeName); type is not null; type = type.BaseType)
        {
            if (type.GetMembers(member).OfType<IEventSymbol>().Any())
            {
                return true;
            }
        }
        return false;
    }

    public string? PropertyType(string typeName, string property)
    {
        for (var type = ByName(typeName); type is not null; type = type.BaseType)
        {
            if (type.GetMembers(property).OfType<IPropertySymbol>().FirstOrDefault() is { } symbol)
            {
                // "string", "global::System.Uri": usable as a C# cast as is.
                return symbol.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }
        return null;
    }

    public bool IsWindow(string typeName) => DerivesFrom(typeName, "Microsoft.UI.Xaml.Window");

    /// <summary>Types reached through x:Bind paths, by display name (constructed generics have no metadata name).</summary>
    private readonly ConcurrentDictionary<string, ITypeSymbol> _pathTypes = new(StringComparer.Ordinal);

    public string? MemberType(string typeName, string member)
    {
        ITypeSymbol? type = _pathTypes.TryGetValue(typeName, out var known) ? known : ByName(typeName);
        if (type is null || type.TypeKind is TypeKind.Dynamic or TypeKind.TypeParameter or TypeKind.Error || type.SpecialType == SpecialType.System_Object)
        {
            return XamlTransform.UnknownType;
        }
        var types = new List<ITypeSymbol>();
        for (var t = type; t is not null; t = t.BaseType)
        {
            types.Add(t);
        }
        types.AddRange(type.AllInterfaces);
        foreach (var candidate in types)
        {
            switch (candidate.GetMembers(member).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol or IMethodSymbol))
            {
                case IPropertySymbol property:
                    return Remember(property.Type);
                case IFieldSymbol field:
                    return Remember(field.Type);
                case IMethodSymbol:
                    // An event handler or function: x:Bind allows it, and the path ends there.
                    return XamlTransform.UnknownType;
            }
        }
        return null;

        string Remember(ITypeSymbol result)
        {
            var name = result.ToDisplayString();
            _pathTypes[name] = result;
            return name;
        }
    }

    public bool IsTemplate(string typeName) => DerivesFrom(typeName, "Microsoft.UI.Xaml.FrameworkTemplate");

    private bool DerivesFrom(string typeName, string baseName)
    {
        for (var type = ByName(typeName); type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == baseName)
            {
                return true;
            }
        }
        return false;
    }
}
