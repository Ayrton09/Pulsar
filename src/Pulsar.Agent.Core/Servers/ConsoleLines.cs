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
        while (true)
        {
            var newline = IndexOf(_pending, '\n');
            var pending = _pending.ToString();
            // A pseudo console repaints lines written together with moves to the start of the next row instead of line
            // breaks: such a move ends a line that has text (a home on an empty line, the first repaint, adds nothing).
            var move = RowStart().Match(pending);
            if (move.Success && (newline < 0 || move.Index < newline))
            {
                var line = Clean(pending[..move.Index]);
                if (line.Length > 0)
                {
                    lines.Add(line);
                }

                _pending.Remove(0, move.Index + move.Length);
                continue;
            }

            if (newline < 0)
            {
                break;
            }

            lines.Add(Clean(pending[..newline]));
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

    // Cursor position (CUP) to column 1 of a row, or home: ESC [ row ; 1 H, ESC [ row H, ESC [ H.
    [GeneratedRegex(@"\x1B\[[0-9]*(?:;1?)?H", RegexOptions.CultureInvariant)]
    private static partial Regex RowStart();
}
