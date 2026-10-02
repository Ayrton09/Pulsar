using System.Text;

namespace Pulsar.Manifests;

/// <summary>
/// The encoders of configuration files (docs/architecture/04, "Configuration files"): each one refuses values that
/// would break out of their line or their value, and writes the declared keys into a file's text.
/// </summary>
public static class ConfigFileWriter
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
        return format == ConfigFileFormat.SourceCfg && value.IndexOfAny(['"', ';']) >= 0
            ? "Quotes and semicolons are not allowed in Source configuration values."
            : null;
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

        var builder = new StringBuilder();
        foreach (var line in output)
        {
            builder.Append(line).Append(newline);
        }

        return builder.ToString();
    }

    private static string Line(ConfigFileFormat format, ConfigEntry entry) => format switch
    {
        ConfigFileFormat.SourceCfg => $"{entry.Key} \"{entry.Value}\"",
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

                var separator = text.IndexOfAny(['=', ':']);
                var key = (separator < 0 ? text : text[..separator]).TrimEnd();
                return key.Length > 0 ? key : null;
        }
    }
}
