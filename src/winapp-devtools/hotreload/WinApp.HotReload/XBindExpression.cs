// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;

namespace WinApp.HotReload;

/// <summary>
/// Translates an x:Bind path that uses functions or casts (<c>local:Format.Count(ViewModel.Items.Count)</c>,
/// <c>(x:String)Tag</c>) into a C# expression over a source variable, plus the property paths whose change notifications
/// re-evaluate it. Returns null for shapes it doesn't understand, so callers can report them instead of guessing.
/// </summary>
internal sealed class XBindExpression
{
    private static readonly Dictionary<string, string> XamlTypes = new(StringComparer.Ordinal)
    {
        ["String"] = "string", ["Boolean"] = "bool", ["Int32"] = "int", ["Int64"] = "long", ["Double"] = "double",
        ["Single"] = "float", ["Object"] = "object", ["Char"] = "char", ["Byte"] = "byte", ["Decimal"] = "decimal",
    };

    private readonly string _text;
    private readonly string _source;
    private readonly Func<string, string?> _resolvePrefix;
    private readonly List<string> _watch = [];
    private int _position;

    private XBindExpression(string text, string source, Func<string, string?> resolvePrefix, Func<string, string?>? resolveDefaultType)
    {
        _text = text;
        _source = source;
        _resolvePrefix = resolvePrefix;
        _resolveDefaultType = resolveDefaultType;
    }

    private readonly Func<string, string?>? _resolveDefaultType;

    public required string CSharp { get; init; }
    public IReadOnlyList<string> Watch { get; private init; } = [];

    /// <param name="path">The x:Bind path, e.g. <c>local:Format.Count(ViewModel.Items.Count)</c>.</param>
    /// <param name="source">The C# variable paths are relative to.</param>
    /// <param name="resolvePrefix">Maps an XML namespace prefix to a CLR namespace (<c>x</c> is built in).</param>
    /// <param name="resolveDefaultType">Maps a type name without a prefix (<c>(ComboBoxItem)</c>) to its full name.</param>
    public static XBindExpression? Translate(string path, string source, Func<string, string?> resolvePrefix, Func<string, string?>? resolveDefaultType = null)
    {
        var parser = new XBindExpression(path, source, resolvePrefix, resolveDefaultType) { CSharp = "" };
        try
        {
            var expression = parser.ParseExpression();
            parser.SkipSpace();
            return parser._position == path.Length && expression is not null
                ? new XBindExpression(path, source, resolvePrefix, resolveDefaultType) { CSharp = expression, Watch = parser._watch.Distinct().ToList() }
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string? ParseExpression()
    {
        SkipSpace();
        if (Peek() == '(')
        {
            _position++;
            var start = _position;
            // A cast, (prefix:Type)expression, else a parenthesized expression: ((local:Item)List.SelectedItem).Name.
            if (TryReadCastType() is { } castType)
            {
                var operand = ParseExpression();
                return operand is null ? null : $"(({castType}){operand})";
            }
            _position = start;
            var inner = ParseExpression();
            Expect(')');
            return inner is null ? null : $"({inner})" + ReadMembers();
        }
        if (Peek() is '\'' or '"')
        {
            return ReadString();
        }
        if (char.IsDigit(Peek()) || Peek() == '-')
        {
            var start = _position++;
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
            {
                _position++;
            }
            return _text[start.._position];
        }

        var name = ReadQualifiedName();
        if (name is "x:True" or "x:False" or "x:Null")
        {
            return name[2..].ToLowerInvariant();
        }
        SkipSpace();
        var colon = name.IndexOf(':');
        string target;
        string? watchPrefix;
        if (colon > 0)
        {
            // prefix:Class.Method(...) or prefix:Class.StaticProperty
            var clrNamespace = _resolvePrefix(name[..colon]) ?? throw new FormatException();
            target = "global::" + clrNamespace + "." + name[(colon + 1)..];
            watchPrefix = null;
        }
        else
        {
            target = _source + "." + name;
            watchPrefix = name;
        }
        if (Peek() != '(')
        {
            if (watchPrefix is not null)
            {
                _watch.Add(watchPrefix);
            }
            return target;
        }

        // A function call: the path up to the method is watched, as are its arguments.
        if (watchPrefix is not null && watchPrefix.LastIndexOf('.') is > 0 and var dot)
        {
            _watch.Add(watchPrefix[..dot]);
        }
        _position++;
        var arguments = new List<string>();
        SkipSpace();
        while (Peek() != ')')
        {
            arguments.Add(ParseExpression() ?? throw new FormatException());
            SkipSpace();
            if (Peek() == ',')
            {
                _position++;
            }
            else if (Peek() != ')')
            {
                throw new FormatException();
            }
        }
        _position++;
        return $"{target}({string.Join(", ", arguments)})" + ReadMembers();
    }

    /// <summary>
    /// After an opening parenthesis: the type when it is a cast (a type name, then ")" and an operand), else null with
    /// the position left anywhere (the caller resets it).
    /// </summary>
    private string? TryReadCastType()
    {
        SkipSpace();
        var start = _position;
        while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or '.' or ':'))
        {
            _position++;
        }
        if (_position == start)
        {
            return null;
        }
        var name = _text[start.._position];
        SkipSpace();
        if (Peek() != ')')
        {
            return null;
        }
        _position++;
        SkipSpace();
        // A cast is followed by its operand; a parenthesized path by "." or the end.
        if (!(char.IsLetter(Peek()) || Peek() is '_' or '('))
        {
            return null;
        }
        return ResolveType(name);
    }

    /// <summary>".Member.Member" after a parenthesized expression or a call.</summary>
    private string ReadMembers()
    {
        var builder = new StringBuilder();
        while (Peek() == '.')
        {
            _position++;
            var start = _position;
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
            {
                _position++;
            }
            if (_position == start)
            {
                throw new FormatException();
            }
            builder.Append('.').Append(_text, start, _position - start);
        }
        return builder.ToString();
    }

    private string? ResolveType(string name)
    {
        var colon = name.IndexOf(':');
        if (colon < 0)
        {
            return _resolveDefaultType?.Invoke(name) is { } full ? "global::" + full : null;
        }
        var prefix = name[..colon];
        var local = name[(colon + 1)..];
        if (prefix == "x")
        {
            return XamlTypes.GetValueOrDefault(local);
        }
        return _resolvePrefix(prefix) is { } ns ? "global::" + ns + "." + local : null;
    }

    private string ReadQualifiedName()
    {
        SkipSpace();
        var start = _position;
        while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or '.' or ':' or '[' or ']'))
        {
            _position++;
        }
        if (_position == start)
        {
            throw new FormatException();
        }
        return _text[start.._position];
    }

    private string ReadString()
    {
        var quote = _text[_position++];
        var builder = new StringBuilder("\"");
        while (_position < _text.Length && _text[_position] != quote)
        {
            var c = _text[_position++];
            builder.Append(c switch { '"' => "\\\"", '\\' => "\\\\", _ => c.ToString() });
        }
        Expect(quote);
        return builder.Append('"').ToString();
    }

    private char Peek() => _position < _text.Length ? _text[_position] : '\0';

    private void Expect(char c)
    {
        SkipSpace();
        if (Peek() != c)
        {
            throw new FormatException();
        }
        _position++;
    }

    private void SkipSpace()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }
}
