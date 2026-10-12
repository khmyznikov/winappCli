// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinApp.HotReload.Protocol;

namespace WinApp.HotReload;

/// <summary>Answers the type questions the transform cannot answer from markup alone.</summary>
internal interface IXamlTypeResolver
{
    /// <summary>Full metadata name for an element or attached-property owner, or null when unknown.</summary>
    string? Resolve(string xmlNamespace, string localName);
    bool HasEvent(string typeName, string member);
    bool IsWindow(string typeName);
    bool IsTemplate(string typeName);

    /// <summary>True when every type of the XML namespace is known, so an unresolved element there is a typo.</summary>
    bool IsKnownNamespace(string xmlNamespace) => false;

    /// <summary>The full type name of a settable property of the type (inherited ones included), or null.</summary>
    string? PropertyType(string typeName, string property) => null;

    /// <summary>
    /// The type of a property or field an x:Bind path can step through, null when the type has no such member, or
    /// <see cref="XamlTransform.UnknownType"/> when it can't be told (the path isn't checked further).
    /// </summary>
    string? MemberType(string typeName, string member) => XamlTransform.UnknownType;
}

internal sealed record XamlNamedElement(string Name, string? TypeName);

internal sealed class XamlTransformResult
{
    public required string RuntimeXaml { get; init; }
    public required ComponentManifest Manifest { get; init; }
    public required IReadOnlyList<XamlNamedElement> Names { get; init; }
    public string? ClassName { get; init; }
    public string? RootTypeName { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public IReadOnlyList<XBindFunction> Functions { get; init; } = [];
    /// <summary>Problems the XAML compiler would report, so the markup isn't sent to the app.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
    /// <summary>See <see cref="MarkupPayload.LineMap"/>.</summary>
    public List<int[]> LineMap { get; init; } = [];
}

/// <summary>An x:Bind function or cast, compiled into the component as a static method over its binding source.</summary>
/// <param name="BindBack">For two-way bindings: a statement that writes <c>__value</c> back to the source.</param>
internal sealed record XBindFunction(string Method, string Expression, string SourceType, string Markup, string? BindBack = null);

/// <summary>
/// Turns authored XAML into markup the WinUI runtime text parser accepts, plus a manifest of everything only
/// the XAML compiler would have wired (x:Name fields, event handlers, root-scope x:Bind, Window properties).
/// </summary>
internal static class XamlTransform
{
    public const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string GeneratedNamePrefix = "__hr_";

    /// <summary>How messages name an element: its x:Name, or its type when it has none of its own.</summary>
    private static string LabelOf(XElement element, bool isRoot, string? name) =>
        isRoot ? "root" : name is { } n && !n.StartsWith(GeneratedNamePrefix, StringComparison.Ordinal) ? n : element.Name.LocalName;
    public const string UnknownType = "?";
    public const string FunctionPrefix = "__WinAppHotReload_Fn";
    private const string HookPrefix = "__hr";
    private const string HookNamespace = "using:WinApp.DevTools.Managed.HotReload";
    private static readonly XNamespace HookXmlNamespace = HookNamespace;
    private static readonly XNamespace X = XamlNamespace;
    private static readonly HashSet<string> CompileOnlyDirectives = ["DataType", "Phase", "DefaultBindMode", "FieldModifier", "Load", "DeferLoadStrategy", "ConnectionId"];

    /// <summary>Markup extensions the parser implements itself, so they have no public type to resolve.</summary>
    private static readonly HashSet<string> ParserBuiltIns = ["StaticResource", "ThemeResource", "CustomResource", "TemplateBinding", "NullExtension"];

    public static XamlTransformResult Transform(string xaml, IXamlTypeResolver resolver)
    {
        var document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var root = document.Root ?? throw new XmlException("The XAML file has no root element.");
        var className = (string?)root.Attribute(X + "Class");
        var state = new State(resolver, className);
        if (className is not null && !className.Split('.').All(IsIdentifier))
        {
            state.Errors.Add($"({((IXmlLineInfo)root).LineNumber},{((IXmlLineInfo)root).LinePosition}): x:Class '{className}' is not a valid type name");
        }
        var rootType = resolver.Resolve(root.Name.NamespaceName, root.Name.LocalName);
        state.Visit(root, isRoot: true, template: null, defaultMode: null, isWindowRoot: rootType is not null && resolver.IsWindow(rootType));
        state.CheckPaths();
        if (state.Hooks.Count > 0)
        {
            root.SetAttributeValue(XNamespace.Xmlns + HookPrefix, HookNamespace);
        }
        var runtimeXaml = Serialize(document);
        return new XamlTransformResult
        {
            RuntimeXaml = runtimeXaml,
            LineMap = MapLines(root, runtimeXaml),
            Manifest = new ComponentManifest
            {
                ClassName = className ?? "",
                Names = state.Names.Select(name => name.Name).ToList(),
                Events = state.Events,
                Bindings = state.Bindings,
                RootProperties = state.RootProperties,
                Hooks = state.Hooks,
            },
            Functions = state.Functions,
            Errors = state.Errors,
            Names = state.Names,
            ClassName = className,
            RootTypeName = rootType,
            Warnings = state.Warnings,
        };
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Writes the transformed markup with every element and attribute on the line it has in the user's file (start tags
    /// split over lines stay split), so positions the runtime reports (parse errors, DevTools source lines) are right.
    /// </summary>
    internal static string Serialize(XDocument document)
    {
        var builder = new StringBuilder();
        var line = 1;
        var column = 1;
        void Write(string text)
        {
            foreach (var c in text)
            {
                if (c == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            builder.Append(text);
        }
        // Moves to where the node was in the source: further down (and across) only, never back.
        bool MoveTo(XObject node)
        {
            if (node is not IXmlLineInfo info || !info.HasLineInfo() || info.LineNumber <= line)
            {
                return false;
            }
            Write(new string('\n', info.LineNumber - line));
            Write(new string(' ', Math.Max(0, info.LinePosition - 1 - (node is XElement ? 1 : 0))));
            return true;
        }
        static string Escape(string value, bool attribute)
        {
            var result = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                result.Append(c switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' when !attribute => "&gt;",
                    '"' when attribute => "&quot;",
                    '\n' when attribute => "&#xA;",
                    '\r' when attribute => "&#xD;",
                    '\t' when attribute => "&#x9;",
                    _ => c.ToString(),
                });
            }
            return result.ToString();
        }
        static string ElementName(XElement element)
        {
            var prefix = element.Name.Namespace == XNamespace.None || element.Name.Namespace == element.GetDefaultNamespace()
                ? null : element.GetPrefixOfNamespace(element.Name.Namespace);
            return prefix is null ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
        }
        static string AttributeName(XAttribute attribute)
        {
            if (attribute.IsNamespaceDeclaration)
            {
                return attribute.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + attribute.Name.LocalName;
            }
            var ns = attribute.Name.Namespace;
            return ns == XNamespace.None ? attribute.Name.LocalName
                : (attribute.Parent?.GetPrefixOfNamespace(ns) ?? throw new XmlException($"No prefix for {ns}")) + ":" + attribute.Name.LocalName;
        }
        void WriteNode(XNode node)
        {
            switch (node)
            {
                case XElement element:
                    MoveTo(element);
                    Write("<" + ElementName(element));
                    foreach (var attribute in element.Attributes())
                    {
                        if (!MoveTo(attribute))
                        {
                            Write(" ");
                        }
                        Write($"{AttributeName(attribute)}=\"{Escape(attribute.Value, attribute: true)}\"");
                    }
                    if (element.IsEmpty)
                    {
                        Write(" />");
                        break;
                    }
                    Write(">");
                    foreach (var child in element.Nodes())
                    {
                        WriteNode(child);
                    }
                    Write("</" + ElementName(element) + ">");
                    break;
                case XCData cdata:
                    Write(cdata.ToString());
                    break;
                case XText text:
                    Write(Escape(text.Value, attribute: false));
                    break;
                default:
                    Write(node.ToString());
                    break;
            }
        }
        if (document.Declaration is { } declaration)
        {
            Write(declaration.ToString());
        }
        WriteNode(document.Root!);
        return builder.ToString();
    }

    /// <summary>
    /// Where each element and attribute of the runtime markup came from: start tags written on several lines come out
    /// on one, so the runtime's parse errors are mapped back to the user's file.
    /// </summary>
    private static List<int[]> MapLines(XElement transformed, string runtimeXaml)
    {
        var map = new List<int[]>();
        XElement emitted;
        try
        {
            emitted = XElement.Parse(runtimeXaml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return map;
        }
        void Add(IXmlLineInfo to, IXmlLineInfo from)
        {
            if (to.HasLineInfo() && from.HasLineInfo())
            {
                map.Add([to.LineNumber, to.LinePosition, from.LineNumber, from.LinePosition]);
            }
        }
        foreach (var (to, from) in emitted.DescendantsAndSelf().Zip(transformed.DescendantsAndSelf()))
        {
            Add(to, from);
            foreach (var attribute in to.Attributes())
            {
                if (from.Attribute(attribute.Name) is { } original)
                {
                    Add(attribute, original);
                }
            }
        }
        return map.OrderBy(m => m[0]).ThenBy(m => m[1]).ToList();
    }

    private sealed class State(IXamlTypeResolver resolver, string? className)
    {
        private int _generatedNames;
        public List<XamlNamedElement> Names { get; } = [];
        public List<EventWiring> Events { get; } = [];
        public List<RootBinding> Bindings { get; } = [];
        public Dictionary<string, string> RootProperties { get; } = [];
        public Dictionary<string, HookSpec> Hooks { get; } = [];
        public List<XBindFunction> Functions { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        /// <summary>x:Bind paths to check once every x:Name is known: the source type (null: the component) and position.</summary>
        private readonly List<(string Path, string? Source, int Line, int Column)> _paths = [];

        /// <summary>Reports an x:Bind path the compiler would reject (WMC1110), segment by segment.</summary>
        public void CheckPaths()
        {
            foreach (var (path, source, line, column) in _paths)
            {
                if (path.Length == 0 || path.IndexOfAny(['(', ':']) >= 0)
                {
                    continue;
                }
                var segments = path.Split('.');
                var start = 0;
                var type = source;
                if (type is null)
                {
                    if (Names.FirstOrDefault(n => n.Name == segments[0]) is { } named)
                    {
                        type = named.TypeName;
                        start = 1;
                    }
                    else
                    {
                        type = className;
                    }
                }
                for (var i = start; i < segments.Length && type is not null && type != UnknownType; i++)
                {
                    if (segments[i].Contains('[') || segments[i].Length == 0)
                    {
                        break;
                    }
                    var next = resolver.MemberType(type, segments[i]);
                    if (next is null)
                    {
                        Errors.Add($"({line},{column}): Invalid binding path '{path}' : Property '{segments[i]}' not found on type '{type[(type.LastIndexOf('.') + 1)..]}'.");
                        break;
                    }
                    type = next;
                }
            }
        }

        private void CheckPath(string path, string? source, XAttribute attribute) =>
            _paths.Add((path, source, ((IXmlLineInfo)attribute).LineNumber, ((IXmlLineInfo)attribute).LinePosition));

        /// <summary>x:Names declared in the template <paramref name="element"/> is in.</summary>
        private HashSet<string> TemplateNames(XElement element)
        {
            var template = element.AncestorsAndSelf().FirstOrDefault(e => !e.Name.LocalName.Contains('.') &&
                resolver.Resolve(e.Name.NamespaceName, e.Name.LocalName) is { } t && resolver.IsTemplate(t));
            return (template?.Descendants() ?? []).Select(e => (string?)e.Attribute(X + "Name") ?? (string?)e.Attribute("Name")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }

        /// <param name="template">Null outside templates; inside one, the item type its x:DataType names (or "" when it has none).</param>
        public void Visit(XElement element, bool isRoot, string? template, string? defaultMode, bool isWindowRoot)
        {
            if (element.Name.LocalName.Contains('.'))
            {
                foreach (var child in element.Elements().ToList())
                {
                    Visit(child, false, template, defaultMode, false);
                }
                return;
            }

            var typeName = resolver.Resolve(element.Name.NamespaceName, element.Name.LocalName);
            var line = ((IXmlLineInfo)element).LineNumber;
            if (typeName is null && resolver.IsKnownNamespace(element.Name.NamespaceName) && !ParserBuiltIns.Contains(element.Name.LocalName))
            {
                Errors.Add($"({line},{((IXmlLineInfo)element).LinePosition}): Unknown type '{element.Name.LocalName}' in XML namespace '{element.Name.NamespaceName}'");
            }
            var nameAttribute = element.Attribute(X + "Name") ?? element.Attribute("Name");
            string? elementName = nameAttribute?.Value;
            if (element.Attribute(X + "DefaultBindMode") is { } bindMode)
            {
                defaultMode = bindMode.Value;
            }
            var inTemplate = template is not null;

            if (elementName is not null && !IsIdentifier(elementName))
            {
                Errors.Add($"({line},{((IXmlLineInfo)element).LinePosition}): x:Name '{elementName}' is not a valid name");
            }
            if (elementName is not null && !isRoot && !inTemplate)
            {
                Names.Add(new XamlNamedElement(elementName, typeName));
            }
            if (!isRoot && string.Equals((string?)element.Attribute(X + "Load"), "False", StringComparison.OrdinalIgnoreCase))
            {
                // Deferred: not created until code asks for it, so its field stays null.
                element.Remove();
                return;
            }
            if (typeName is not null && resolver.IsTemplate(typeName))
            {
                template = element.Attribute(X + "DataType") is { } dataType ? ResolveTypeName(element, dataType.Value) ?? "" : "";
            }

            HookSpec? hook = null;
            HookSpec Hook()
            {
                if (hook is null)
                {
                    hook = new HookSpec();
                    var id = $"{className}#{Hooks.Count}";
                    Hooks[id] = hook;
                    element.SetAttributeValue(HookXmlNamespace + "Hook.Id", id);
                }
                return hook;
            }

            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }
                if (attribute.Name.Namespace == X)
                {
                    if (CompileOnlyDirectives.Contains(attribute.Name.LocalName))
                    {
                        attribute.Remove();
                    }
                    continue;
                }
                if (attribute.Name.Namespace != XNamespace.None && attribute.Name.Namespace != element.Name.Namespace)
                {
                    continue;
                }

                var value = attribute.Value;
                var isEvent = typeName is not null && !attribute.Name.LocalName.Contains('.') && resolver.HasEvent(typeName, attribute.Name.LocalName);
                if (isEvent && value.StartsWith("{x:Bind", StringComparison.Ordinal))
                {
                    // Event binding: the handler is a method on an object the path leads to.
                    attribute.Remove();
                    var path = ParseBind(value, defaultMode)?.Path ?? "";
                    var dot = path.LastIndexOf('.');
                    if (path.Length == 0 || path.Contains('(') || path.Contains(':'))
                    {
                        Warnings.Add($"line {line}: '{value}' isn't an event binding live reload can wire; the event stays unhandled until restart.");
                        continue;
                    }
                    var wiring = new EventWiring { Event = attribute.Name.LocalName, Handler = dot < 0 ? path : path[(dot + 1)..], Path = dot < 0 ? "" : path[..dot], Label = LabelOf(element, isRoot, elementName) };
                    if (inTemplate)
                    {
                        Hook().Events.Add(wiring);
                    }
                    else
                    {
                        wiring.Element = isRoot ? null : elementName ??= AssignName(element);
                        Events.Add(wiring);
                    }
                    continue;
                }
                if (value.StartsWith("{x:Bind", StringComparison.Ordinal))
                {
                    attribute.Remove();
                    var bind = ParseBind(value, defaultMode);
                    if (bind is null)
                    {
                        Warnings.Add($"line {line}: '{value}' isn't x:Bind syntax live reload understands; the property stays unset until restart.");
                        continue;
                    }
                    var (owner, property) = SplitAttached(attribute.Name.LocalName, element.Name.NamespaceName);
                    bind.Property = property;
                    bind.OwnerType = owner;
                    if (bind.Path.Contains('(') || bind.Path.Contains(':'))
                    {
                        // Functions, casts and static members ({x:Bind local:AppFacts.StaticBanner}) are compiled.
                        var source = inTemplate ? template : className;
                        var expression = string.IsNullOrEmpty(source) ? null
                            : XBindExpression.Translate(bind.Path, "__s", prefix => ResolveNamespace(element, prefix));
                        if (expression is null)
                        {
                            Warnings.Add($"line {line}: '{value}' can't be evaluated live (unsupported function syntax{(inTemplate && string.IsNullOrEmpty(template) ? " or a template without x:DataType" : "")}); the property stays unset until restart.");
                            continue;
                        }
                        bind.Function = FunctionPrefix + Functions.Count;
                        bind.Watch = expression.Watch.ToList();
                        string? back = null;
                        if (bind.BindBackPath is { Length: > 0 } backPath && !backPath.Contains('('))
                        {
                            var valueType = typeName is null ? null : resolver.PropertyType(typeName, property);
                            var argument = valueType is null ? "__value" : $"({valueType})__value";
                            back = backPath.Contains(':')
                                ? XBindExpression.Translate(backPath, "__s", prefix => ResolveNamespace(element, prefix))?.CSharp + $"({argument});"
                                : $"__s.{backPath}({argument});";
                            bind.BindBack = bind.Function + "_Back";
                        }
                        Functions.Add(new XBindFunction(bind.Function, expression.CSharp, source!, value, back));
                        if (inTemplate)
                        {
                            Hook().Bindings.Add(bind);
                            continue;
                        }
                    }
                    else if (inTemplate)
                    {
                        // {x:Bind NameBox.Text} inside a template refers to an element of that template.
                        var head = bind.Path.Split('.')[0];
                        if (head.Length > 0 && TemplateNames(element).Contains(head))
                        {
                            bind.ElementName = head;
                            bind.Path = bind.Path.Length > head.Length ? bind.Path[(head.Length + 1)..] : "";
                        }
                        else if (!string.IsNullOrEmpty(template))
                        {
                            CheckPath(bind.Path, template, attribute);
                        }
                        element.SetAttributeValue(attribute.Name, ToBinding(bind));
                        continue;
                    }
                    bind.Element = isRoot ? null : elementName ??= AssignName(element);
                    if (bind.Function is null && className is not null)
                    {
                        CheckPath(bind.Path, null, attribute);
                    }
                    Bindings.Add(bind);
                    continue;
                }

                if (isEvent)
                {
                    attribute.Remove();
                    if (inTemplate)
                    {
                        // Wired to the component's method when the templated element is created.
                        Hook().Events.Add(new EventWiring { Event = attribute.Name.LocalName, Handler = value, Label = LabelOf(element, isRoot, elementName) });
                        continue;
                    }
                    Events.Add(new EventWiring { Label = LabelOf(element, isRoot, elementName), Element = isRoot ? null : elementName ??= AssignName(element), Event = attribute.Name.LocalName, Handler = value });
                    continue;
                }

                if (isWindowRoot)
                {
                    attribute.Remove();
                    RootProperties[attribute.Name.LocalName] = value;
                }
            }

            foreach (var child in element.Elements().ToList())
            {
                Visit(child, false, template, defaultMode, false);
            }
        }

        private static string? ResolveNamespace(XElement element, string prefix)
        {
            var ns = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            return ns is null ? null
                : ns.StartsWith("using:", StringComparison.Ordinal) ? ns["using:".Length..]
                : ns.StartsWith("clr-namespace:", StringComparison.Ordinal) ? ns["clr-namespace:".Length..].Split(';')[0]
                : null;
        }

        private string? ResolveTypeName(XElement element, string qualified)
        {
            var colon = qualified.IndexOf(':');
            return colon < 0
                ? resolver.Resolve(element.GetDefaultNamespace().NamespaceName, qualified)
                : ResolveNamespace(element, qualified[..colon]) is { } ns ? ns + "." + qualified[(colon + 1)..] : null;
        }
        private string AssignName(XElement element)
        {
            var name = GeneratedNamePrefix + _generatedNames++;
            element.SetAttributeValue(X + "Name", name);
            return name;
        }

        private (string? Owner, string Property) SplitAttached(string attributeName, string elementNamespace)
        {
            var dot = attributeName.LastIndexOf('.');
            if (dot < 0)
            {
                return (null, attributeName);
            }
            var ownerName = attributeName[..dot];
            return (resolver.Resolve(elementNamespace, ownerName) ?? ownerName, attributeName[(dot + 1)..]);
        }
    }

    /// <summary>
    /// App.xaml's &lt;Application.Resources&gt; as a standalone ResourceDictionary the runtime can parse on its own.
    /// Relative dictionary sources are made absolute, since the result is loaded without a base URI.
    /// </summary>
    public static string ApplicationResources(string runtimeXaml, string appUri)
    {
        var root = XElement.Parse(runtimeXaml);
        var content = root.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal))?.Elements().ToList() ?? [];
        var presentation = root.Name.Namespace;
        var dictionary = content is [{ } single] && single.Name == presentation + "ResourceDictionary"
            ? new XElement(single)
            : new XElement(presentation + "ResourceDictionary", content);
        foreach (var declaration in root.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            if (dictionary.Attribute(declaration.Name) is null)
            {
                dictionary.SetAttributeValue(declaration.Name, declaration.Value);
            }
        }
        var baseUri = new Uri(appUri);
        foreach (var source in dictionary.DescendantsAndSelf().Where(e => e.Name.LocalName == "ResourceDictionary").Select(e => e.Attribute("Source")).OfType<XAttribute>())
        {
            if (!source.Value.Contains(':'))
            {
                source.Value = new Uri(baseUri, source.Value).ToString();
            }
        }
        return dictionary.ToString(SaveOptions.DisableFormatting);
    }

    internal static RootBinding? ParseBind(string markup, string? defaultMode)
    {
        var body = markup.Trim();
        if (!body.StartsWith("{x:Bind", StringComparison.Ordinal) || !body.EndsWith('}'))
        {
            return null;
        }
        body = body["{x:Bind".Length..^1];
        var result = new RootBinding { Mode = defaultMode ?? "OneTime" };
        var first = true;
        foreach (var rawPart in SplitTopLevel(body))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                first = false;
                continue;
            }
            var equals = IndexOfTopLevel(part, '=');
            if (equals < 0)
            {
                if (!first)
                {
                    return null;
                }
                result.Path = part;
            }
            else
            {
                var key = part[..equals].Trim();
                var value = part[(equals + 1)..].Trim();
                switch (key)
                {
                    case "Path": result.Path = value; break;
                    case "Mode": result.Mode = value; break;
                    case "Converter": result.Converter = value; break;
                    case "ConverterParameter": result.ConverterParameter = value; break;
                    case "FallbackValue": result.FallbackValue = value; break;
                    case "TargetNullValue": result.TargetNullValue = value; break;
                    case "BindBack": result.BindBackPath = value; break;
                    case "UpdateSourceTrigger": result.UpdateSourceTrigger = value; break;
                    // ConverterLanguage has no live equivalent worth faking.
                    default: break;
                }
            }
            first = false;
        }
        return result;
    }

    internal static string ToBinding(RootBinding bind)
    {
        var builder = new StringBuilder("{Binding");
        var parts = new List<string>();
        if (bind.Path.Length > 0)
        {
            parts.Add(bind.Path);
        }
        if (bind.ElementName is not null) parts.Add("ElementName=" + bind.ElementName);
        parts.Add("Mode=" + bind.Mode);
        if (bind.UpdateSourceTrigger is not null) parts.Add("UpdateSourceTrigger=" + bind.UpdateSourceTrigger);
        if (bind.Converter is not null) parts.Add("Converter=" + bind.Converter);
        if (bind.ConverterParameter is not null) parts.Add("ConverterParameter=" + bind.ConverterParameter);
        if (bind.FallbackValue is not null) parts.Add("FallbackValue=" + bind.FallbackValue);
        if (bind.TargetNullValue is not null) parts.Add("TargetNullValue=" + bind.TargetNullValue);
        builder.Append(' ').Append(string.Join(", ", parts)).Append('}');
        return builder.ToString();
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{' or '(': depth++; break;
                case '}' or ')': depth--; break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return text[start..];
    }

    private static int IndexOfTopLevel(string text, char value)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '{' or '(') depth++;
            else if (text[i] is '}' or ')') depth--;
            else if (text[i] == value && depth == 0) return i;
        }
        return -1;
    }
}
