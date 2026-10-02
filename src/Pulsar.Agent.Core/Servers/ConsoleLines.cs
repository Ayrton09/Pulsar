using System.Text;
using System.Text.RegularExpressions;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Turns console chunks into plain text lines: a pseudo console sends VT sequences (colors, cursor moves, titles) and
/// splits lines anywhere, so patterns are matched on whole lines with the escapes removed.
/// </summary>
public sealed partial class ConsoleLines
{
    private const int MaxLineLength = 16 * 1024;

    private readonly StringBuilder _pending = new();

    /// <summary>The complete lines in <paramref name="text"/> (with what earlier chunks left unfinished).</summary>
    public IReadOnlyList<string> Add(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _pending.Append(text);
        var lines = new List<string>();
        int newline;
        while ((newline = IndexOf(_pending, '\n')) >= 0)
        {
            lines.Add(Clean(_pending.ToString(0, newline)));
            _pending.Remove(0, newline + 1);
        }

        // A process that never ends its line still cannot grow this without bound.
        if (_pending.Length > MaxLineLength)
        {
            lines.Add(Clean(_pending.ToString()));
            _pending.Clear();
        }

        return lines;
    }

    /// <summary>Removes VT escape sequences and carriage returns.</summary>
    public static string Clean(string text) => Escapes().Replace(text, "").Replace("\r", "", StringComparison.Ordinal);

    private static int IndexOf(StringBuilder builder, char value)
    {
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL or ESC \), and two-character escapes.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex Escapes();
}
