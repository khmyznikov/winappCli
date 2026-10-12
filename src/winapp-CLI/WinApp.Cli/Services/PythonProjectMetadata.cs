// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Syntax;

namespace WinApp.Cli.Services;

internal static partial class PythonProjectMetadata
{
    private const int MaxMetadataBytes = 512 * 1024;

    public static string? FindMarker(DirectoryInfo directory, bool includeScriptProjects = true)
    {
        try
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || File.Exists(Path.Join(directory.FullName, "pyvenv.cfg")))
            {
                return null;
            }

            if (TryRead(directory, "pyproject.toml", out var pyproject))
            {
                var syntax = Toml.Parse(pyproject);
                if (!syntax.HasErrors)
                {
                    if (HasName(syntax, ["project", "name"])
                        || HasName(syntax, ["tool", "poetry", "name"]))
                    {
                        return "pyproject.toml";
                    }
                }
            }

            if (TryRead(directory, "setup.cfg", out var configuration) && HasSetupName(configuration))
            {
                return "setup.cfg";
            }

            if (includeScriptProjects && TryRead(directory, "setup.py", out var setup)
                && !string.IsNullOrWhiteSpace(setup))
            {
                return "setup.py";
            }

            if (includeScriptProjects && TryRead(directory, "requirements.txt", out _)
                && directory.EnumerateFiles("*.py", SearchOption.TopDirectoryOnly)
                    .Any(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                return "requirements.txt";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // Unreadable markers are not evidence of a project, just as for other detected types.
        }

        return null;
    }

    private static bool HasName(DocumentSyntax document, string[] path)
    {
        if (HasName(document.KeyValues, path))
        {
            return true;
        }
        foreach (var table in document.Tables.OfType<TableSyntax>())
        {
            var key = KeyParts(table.Name);
            if (key.Length < path.Length && key.SequenceEqual(path.Take(key.Length))
                && HasName(table.Items, path[key.Length..]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasName(IEnumerable<KeyValueSyntax> values, string[] path)
    {
        foreach (var item in values)
        {
            var key = KeyParts(item.Key);
            if (key.Length == 0 || key.Length > path.Length || !key.SequenceEqual(path.Take(key.Length)))
            {
                continue;
            }
            if (key.Length == path.Length && item.Value is StringValueSyntax { Value: { } name }
                && ProjectName().IsMatch(name))
            {
                return true;
            }
            if (key.Length < path.Length && item.Value is InlineTableSyntax inline
                && HasName(inline.Items.Select(entry => entry.KeyValue).OfType<KeyValueSyntax>(), path[key.Length..]))
            {
                return true;
            }
        }
        return false;
    }

    private static string?[] KeyParts(KeySyntax? key) => key is null
        ? []
        : [KeyPart(key.Key), .. key.DotKeys.Select(part => KeyPart(part.Key))];

    private static string? KeyPart(BareKeyOrStringValueSyntax? value) => value switch
    {
        BareKeySyntax bare => bare.Key?.Text,
        StringValueSyntax text => text.Value,
        _ => null,
    };

    private static bool HasSetupName(string text)
    {
        var metadata = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                metadata = trimmed.Equals("[metadata]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!metadata || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            var separator = trimmed.IndexOfAny(['=', ':']);
            if (separator > 0
                && trimmed[..separator].Trim().Equals("name", StringComparison.OrdinalIgnoreCase)
                && ProjectName().IsMatch(trimmed[(separator + 1)..].Trim()))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryRead(DirectoryInfo directory, string name, out string text)
    {
        text = string.Empty;
        var file = new FileInfo(Path.Join(directory.FullName, name));
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaxMetadataBytes)
        {
            return false;
        }

        using var stream = file.OpenRead();
        var bytes = new byte[MaxMetadataBytes + 1];
        var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length > MaxMetadataBytes)
        {
            return false;
        }
        text = new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('\uFEFF');
        return true;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectName();
}
