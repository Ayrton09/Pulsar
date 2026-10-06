using System.Text;

namespace Pulsar.Manifests;

/// <summary>
/// Applies an addon's <see cref="LineEdit"/>s to a file's text (docs/architecture/04, "Addons"): the line goes right after
/// the first line that starts with the anchor, compared without leading and trailing spaces and with runs of spaces and
/// tabs as one; it is not added twice. The file's line endings are kept.
/// </summary>
public static class LineEditor
{
    /// <summary>The text with <paramref name="edit"/> applied.</summary>
    /// <exception cref="LaunchPlanException">The anchor is not in the file.</exception>
    public static string Apply(string text, LineEdit edit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edit);
        var (lines, newline) = Split(text);
        var anchor = Anchor(lines, edit);
        if (anchor < 0)
        {
            throw new LaunchPlanException($"{edit.Path}: no line starting with '{edit.After}' to add '{edit.Line}' after.");
        }

        if (anchor + 1 < lines.Count && Normalize(lines[anchor + 1]) == Normalize(edit.Line))
        {
            return text;
        }

        // The addon's line with the anchor's indentation.
        var indent = lines[anchor][..(lines[anchor].Length - lines[anchor].TrimStart().Length)];
        lines.Insert(anchor + 1, indent + edit.Line);
        return Join(lines, newline);
    }

    /// <summary>The text without the line <paramref name="edit"/> added (an uninstalled addon); unchanged when it is not there.</summary>
    public static string Remove(string text, LineEdit edit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edit);
        var (lines, newline) = Split(text);
        var anchor = Anchor(lines, edit);
        if (anchor < 0 || anchor + 1 >= lines.Count || Normalize(lines[anchor + 1]) != Normalize(edit.Line))
        {
            return text;
        }

        lines.RemoveAt(anchor + 1);
        return Join(lines, newline);
    }

    private static int Anchor(List<string> lines, LineEdit edit)
    {
        var after = Normalize(edit.After);
        return lines.FindIndex(line => Normalize(line).StartsWith(after, StringComparison.Ordinal));
    }

    private static (List<string> Lines, string Newline) Split(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return ([.. text.Split('\n').Select(l => l.TrimEnd('\r'))], newline);
    }

    /// <summary>The lines joined again; a text that ended with a newline still does (its last element is empty).</summary>
    private static string Join(List<string> lines, string newline)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(lines[i]);
            if (i < lines.Count - 1)
            {
                builder.Append(newline);
            }
        }

        return builder.ToString();
    }

    private static string Normalize(string line)
    {
        var builder = new StringBuilder(line.Length);
        var space = false;
        foreach (var c in line.Trim())
        {
            if (c is ' ' or '\t')
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }
}
