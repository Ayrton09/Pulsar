using System.Text;
using System.Text.RegularExpressions;

namespace Pulsar.Manifests;

/// <summary>
/// The encoders of configuration files (docs/architecture/04, "Configuration files"): each one refuses values that
/// would break out of their line or their value, and writes the declared keys into a file's text.
/// </summary>
public static partial class ConfigFileWriter
{
    /// <summary>Largest configuration file the agent reads and rewrites.</summary>
    public const int MaximumSize = 1024 * 1024;

    /// <summary>Null when <paramref name="value"/> is safe in <paramref name="format"/>, otherwise why not.</summary>
    public static string? ValueError(ConfigFileFormat format, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
        {
            return "Line breaks and control characters are not allowed.";
        }

        // In Source, ';' separates commands and '"' ends the value: 'hostname "x"; rcon_password y' would run a second command.
        return format switch
        {
            ConfigFileFormat.SourceCfg when value.IndexOfAny(['"', ';']) >= 0 => "Quotes and semicolons are not allowed in Source configuration values.",
            // Unreal reads '\' as an escape inside quotes, and '"' would end the value early.
            ConfigFileFormat.UnrealIni when value.IndexOfAny(['"', '\\']) >= 0 => "Quotes and backslashes are not allowed in Unreal configuration values.",
            // A YAML reader takes these for a list, a map, a quoted text or a comment rather than the value as typed.
            ConfigFileFormat.Colon when value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || value[0] is '[' or '{' or '"' or '\'' or '#' or '&' or '*' or '!' or '|' or '>' or '%' or '@' or '`'
                || value.StartsWith("- ", StringComparison.Ordinal) || value.Contains(" #", StringComparison.Ordinal) || value.Contains(": ", StringComparison.Ordinal))
                => "This value would not be read as written in this configuration file (it starts or ends with a space, or looks like a list, a map, a quoted text or a comment).",
            _ => null,
        };
    }

    /// <summary>
    /// The new text of the file. <paramref name="existing"/> is its current text (null when missing). Patch mode
    /// changes only the declared keys (the first line setting each one is replaced, repeated ones are removed, missing
    /// ones are appended); an empty value removes the key. Declared keys belong to Pulsar: a value written there by hand
    /// is replaced at the next start.
    /// </summary>
    public static string Apply(RenderedConfigFile file, string? existing)
    {
        ArgumentNullException.ThrowIfNull(file);
        foreach (var entry in file.Entries)
        {
            if (ValueError(file.Format, entry.Value) is { } error)
            {
                throw new LaunchPlanException($"{file.Path} {entry.Key}: {error}");
            }
        }

        var newline = existing?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";
        if (file.Format == ConfigFileFormat.UnrealIni)
        {
            return Join(UnrealIni(file, file.Mode == ConfigFileMode.Patch ? existing : null), newline);
        }

        if (file.Format == ConfigFileFormat.XmlProperties)
        {
            return Join(XmlProperties(file, existing), newline);
        }

        var output = new List<string>();
        var pending = file.Entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (file.Mode == ConfigFileMode.Patch && existing is not null)
        {
            var lines = existing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            foreach (var line in lines)
            {
                var key = KeyOf(file.Format, line);
                if (key is null || !pending.TryGetValue(key, out var entry))
                {
                    output.Add(line);
                    continue;
                }

                if (entry.Value.Length > 0 && written.Add(entry.Key))
                {
                    output.Add(Line(file.Format, entry));
                }
            }
        }
        else if (file.Mode == ConfigFileMode.Managed)
        {
            output.Add(file.Format == ConfigFileFormat.SourceCfg
                ? "// Written by Pulsar before every start: changes made here are lost."
                : "# Written by Pulsar before every start: changes made here are lost.");
        }

        foreach (var entry in file.Entries)
        {
            if (entry.Value.Length > 0 && written.Add(entry.Key))
            {
                output.Add(Line(file.Format, entry));
            }
        }

        return Join(output, newline);
    }

    private static string Join(List<string> lines, string newline)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append(newline);
        }

        return builder.ToString();
    }

    private static List<string> Lines(string? text)
    {
        if (text is null)
        {
            return [];
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>
    /// The declared keys written into their section: plain <c>Key=Value</c> lines, or the members of one
    /// <c>Struct=(...)</c> line, whose other members (the users' own settings) stay. Other sections stay as they are.
    /// </summary>
    private static List<string> UnrealIni(RenderedConfigFile file, string? existing)
    {
        var lines = Lines(existing);
        if (existing is null && file.Mode == ConfigFileMode.Managed)
        {
            lines.Add("; Written by Pulsar before every start: changes made here are lost.");
        }

        var header = $"[{file.Section}]";
        var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0 && lines[^1][0] != ';')
            {
                lines.Add("");
            }

            lines.Add(header);
            start = lines.Count - 1;
        }

        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        end = end < 0 ? lines.Count : end;
        var section = lines.GetRange(start + 1, end - start - 1);
        var trailing = section.Count - 1 - section.FindLastIndex(l => l.Trim().Length > 0); // blank lines before the next section
        section.RemoveRange(section.Count - trailing, trailing);

        if (file.Struct is { } name)
        {
            var at = section.FindIndex(l => UnrealKey(l)?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
            List<string> members = at < 0 ? [] : Members(file, section[at]);
            var line = $"{name}=({string.Join(',', Patch(members, file.Entries, UnrealMember))})";
            if (at < 0)
            {
                section.Add(line);
            }
            else
            {
                section[at] = line;
            }
        }
        else
        {
            section = Patch(section, file.Entries, e => $"{e.Key}={e.Value}");
        }

        section.AddRange(Enumerable.Repeat("", trailing));
        lines.RemoveRange(start + 1, end - start - 1);
        lines.InsertRange(start + 1, section);
        return lines;
    }

    /// <summary>
    /// The declared keys as <c>&lt;property name="Key" value="Value"/&gt;</c> elements: the first element (outside
    /// comments) for each key gets the new value and keeps the rest of its line (the game's comment), repeats and
    /// empty values are removed (the rest of their line, a comment included, stays), missing keys go before the root's
    /// closing tag. A missing file starts as an empty root.
    /// </summary>
    private static List<string> XmlProperties(RenderedConfigFile file, string? existing)
    {
        var lines = Lines(existing ?? $"<?xml version=\"1.0\"?>\n<{file.Section}>\n</{file.Section}>\n");
        var pending = file.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        var inComment = false;
        var close = -1;
        foreach (var line in lines)
        {
            var commented = inComment;
            inComment = InComment(line, line.Length, commented);
            // Every property element of the line that is not inside a comment and sets a declared key.
            var patched = new StringBuilder();
            var last = 0;
            var removed = false;
            foreach (Match match in XmlProperty().Matches(line))
            {
                if (InComment(line, match.Index, commented) || !pending.TryGetValue(match.Groups["key"].Value, out var entry))
                {
                    continue;
                }

                patched.Append(line, last, match.Index - last);
                if (entry.Value.Length > 0 && written.Add(entry.Key))
                {
                    patched.Append(XmlElement(entry));
                }
                else
                {
                    removed = true;
                }

                last = match.Index + match.Length;
            }

            var result = patched.Append(line, last, line.Length - last).ToString();
            if (removed && result.Trim().Length == 0)
            {
                continue;
            }

            // Other elements and the comment on the same line stay (a comment may go on over the next lines).
            if (!commented && result.TrimStart().StartsWith($"</{file.Section}>", StringComparison.Ordinal))
            {
                close = output.Count;
            }

            output.Add(result);
        }

        var missing = file.Entries.Where(e => e.Value.Length > 0 && written.Add(e.Key)).Select(e => "\t" + XmlElement(e)).ToList();
        if (missing.Count > 0)
        {
            if (close < 0)
            {
                throw new LaunchPlanException($"{file.Path}: no </{file.Section}> line to add settings before; fix the file or delete it.");
            }

            output.InsertRange(close, missing);
        }

        return output;
    }

    /// <summary>Whether position <paramref name="end"/> of the line is inside an XML comment (<c>&lt;!-- … --&gt;</c>).</summary>
    private static bool InComment(string line, int end, bool startsInComment)
    {
        var inside = startsInComment;
        for (var i = 0; i < end; i++)
        {
            if (inside && string.CompareOrdinal(line, i, "-->", 0, 3) == 0)
            {
                inside = false;
                i += 2;
            }
            else if (!inside && string.CompareOrdinal(line, i, "<!--", 0, 4) == 0)
            {
                inside = true;
                i += 3;
            }
        }

        return inside;
    }

    private static string XmlElement(ConfigEntry entry) =>
        $"<property name=\"{entry.Key}\" value=\"{System.Security.SecurityElement.Escape(entry.Value)}\"/>";

    [GeneratedRegex("""<property\s+name="(?<key>[^"]+)"\s+value="[^"]*"\s*/>""", RegexOptions.CultureInvariant)]
    private static partial Regex XmlProperty();

    /// <summary>Replaces the first item setting each key, drops its repeats and appends the missing ones; empty values remove the key.</summary>
    private static List<string> Patch(List<string> items, IReadOnlyList<ConfigEntry> entries, Func<ConfigEntry, string> write)
    {
        var pending = entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        foreach (var item in items)
        {
            if (UnrealKey(item) is not { } key || !pending.TryGetValue(key, out var entry))
            {
                output.Add(item);
            }
            else if (entry.Value.Length > 0 && written.Add(entry.Key))
            {
                output.Add(write(entry));
            }
        }

        output.AddRange(entries.Where(e => e.Value.Length > 0 && written.Add(e.Key)).Select(write));
        return output;
    }

    /// <summary>Strings are quoted; numbers and booleans are not, as Unreal writes them.</summary>
    private static string UnrealMember(ConfigEntry entry) =>
        UnrealLiteral().IsMatch(entry.Value) ? $"{entry.Key}={entry.Value}" : $"{entry.Key}=\"{entry.Value}\"";

    private static string? UnrealKey(string text)
    {
        var trimmed = text.TrimStart();
        var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
        return trimmed.Length == 0 || trimmed[0] == ';' || separator <= 0 ? null : trimmed[..separator].TrimEnd();
    }

    /// <summary>The members of a <c>Struct=(A=1,B="x, y",C=(D=2))</c> line, split at its top-level commas.</summary>
    private static List<string> Members(RenderedConfigFile file, string line)
    {
        var value = line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim();
        var members = new List<string>();
        var depth = 0;
        var quoted = false;
        var from = 1;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quoted)
            {
                quoted = c != '"' || value[i - 1] == '\\';
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0 && i == value.Length - 1)
                    {
                        if (i > from)
                        {
                            members.Add(value[from..i]);
                        }

                        return members;
                    }

                    break;
                case ',' when depth == 1:
                    members.Add(value[from..i]);
                    from = i + 1;
                    break;
            }

            if (depth <= 0)
            {
                break;
            }
        }

        throw new LaunchPlanException($"{file.Path}: the {file.Struct} line is not a (Key=Value,...) list; fix it or delete the file.");
    }

    // \z, not $: $ also matches before a final line feed.
    [GeneratedRegex(@"^(True|False|-?[0-9]{1,18}(\.[0-9]{1,18})?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnrealLiteral();

    private static string Line(ConfigFileFormat format, ConfigEntry entry) => format switch
    {
        ConfigFileFormat.SourceCfg => $"{entry.Key} \"{entry.Value}\"",
        ConfigFileFormat.KeyValue => $"{entry.Key}={entry.Value}",
        ConfigFileFormat.Colon => $"{entry.Key}: {entry.Value}",
        _ => $"{entry.Key}={entry.Value.Replace("\\", "\\\\", StringComparison.Ordinal)}",
    };

    /// <summary>The key a line sets, or null for comments, blank lines and anything else.</summary>
    public static string? KeyOf(ConfigFileFormat format, string line)
    {
        var text = line.TrimStart();
        if (text.Length == 0)
        {
            return null;
        }

        switch (format)
        {
            case ConfigFileFormat.SourceCfg:
                if (text.StartsWith("//", StringComparison.Ordinal))
                {
                    return null;
                }

                var quoted = text[0] == '"';
                var start = quoted ? 1 : 0;
                var end = start;
                while (end < text.Length && (quoted ? text[end] != '"' : !char.IsWhiteSpace(text[end]) && text[end] != ';'))
                {
                    end++;
                }

                return end > start ? text[start..end] : null;
            default:
                if (text[0] is '#' or '!')
                {
                    return null;
                }

                var separator = format switch
                {
                    ConfigFileFormat.KeyValue => text.IndexOf('=', StringComparison.Ordinal),
                    ConfigFileFormat.Colon => text.IndexOf(':', StringComparison.Ordinal),
                    _ => text.IndexOfAny(['=', ':']),
                };
                var key = (separator < 0 ? text : text[..separator]).TrimEnd();
                return key.Length > 0 ? key : null;
        }
    }
}
