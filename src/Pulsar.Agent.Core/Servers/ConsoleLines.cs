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

    // How far back a move to the start of a row may have begun in earlier chunks (ESC [ row ; 1 H is a few characters).
    private const int MaxMoveLength = 32;

    private readonly StringBuilder _pending = new();

    /// <summary>Tests: how many characters the searches looked at, which stays proportional to what was added.</summary>
    internal long Searched { get; private set; }

    /// <summary>The unfinished line (what no line break has ended yet), without VT sequences: a prompt waiting for input.</summary>
    public string PendingText => Clean(_pending.ToString());

    /// <summary>The complete lines in <paramref name="text"/> (with what earlier chunks left unfinished).</summary>
    public IReadOnlyList<string> Add(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var searched = _pending.Length; // what is pending ends no line: earlier chunks were split already
        _pending.Append(text);
        var lines = new List<string>();

        // Most chunks end no line (a game writing a character at a time): only what they add is searched, with the end of
        // a move that may have begun before, instead of copying and searching everything pending each time.
        var from = Math.Max(0, searched - MaxMoveLength);
        var added = _pending.ToString(from, _pending.Length - from);
        Searched += added.Length;
        if (!added.Contains('\n', StringComparison.Ordinal) && !RowStart().IsMatch(added))
        {
            return Overflow(lines);
        }

        // One pass over the text: each search starts where the last line ended, and the next move found is kept until
        // the lines before it are taken (the pattern has no anchors, so a later start cannot find an earlier match).
        var pending = _pending.ToString();
        Searched += pending.Length;
        var start = 0;
        var move = RowStart().Match(pending);
        while (true)
        {
            var newline = pending.IndexOf('\n', start);
            if (move.Success && move.Index < start)
            {
                move = RowStart().Match(pending, start);
            }

            // A pseudo console repaints lines written together with moves to the start of the next row instead of line
            // breaks: such a move ends a line that has text (a home on an empty line, the first repaint, adds nothing).
            if (move.Success && (newline < 0 || move.Index < newline))
            {
                var line = Clean(pending[start..move.Index]);
                if (line.Length > 0)
                {
                    lines.Add(line);
                }

                start = move.Index + move.Length;
                continue;
            }

            if (newline < 0)
            {
                break;
            }

            lines.Add(Clean(pending[start..newline]));
            start = newline + 1;
        }

        _pending.Remove(0, start);
        return Overflow(lines);
    }

    /// <summary>A process that never ends its line still cannot grow what is pending without bound.</summary>
    private List<string> Overflow(List<string> lines)
    {
        if (_pending.Length > MaxLineLength)
        {
            lines.Add(Clean(_pending.ToString()));
            _pending.Clear();
        }

        return lines;
    }

    /// <summary>Removes VT escape sequences and carriage returns.</summary>
    public static string Clean(string text) => Escapes().Replace(text, "").Replace("\r", "", StringComparison.Ordinal);

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL or ESC \), and two-character escapes.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex Escapes();

    // Cursor position (CUP) to column 1 of a row, or home: ESC [ row ; 1 H, ESC [ row H, ESC [ H.
    [GeneratedRegex(@"\x1B\[[0-9]*(?:;1?)?H", RegexOptions.CultureInvariant)]
    private static partial Regex RowStart();
}
