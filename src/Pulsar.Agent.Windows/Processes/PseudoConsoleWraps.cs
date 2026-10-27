using System.Buffers;
using System.Globalization;
using System.Text;

namespace Pulsar.Agent.Windows.Processes;

/// <summary>
/// Joins the lines a pseudo console wraps at its width (docs/architecture/06, "Console: ConPTY vs pipes"). Once its screen is
/// full, the console host ends a row a long line filled with a line break, moves back to that row's last column, writes
/// its last cell again (so a terminal as wide marks both rows as one line) and goes on with the line:
/// <c>CR LF [colours] ESC [ row ; 220 H</c> and that cell, or <c>; 219 H</c> when it is a wide character. Read as text,
/// that cuts the line in two and doubles the cell, and a link in it no longer opens whole. The line break, the move and
/// the cell written again are taken out; the colours stay. A line exactly as wide as the console ends otherwise
/// (<c>ESC [ row ; 1 H LF</c>) and is not joined to the next.
/// </summary>
internal sealed class PseudoConsoleWraps
{
    /// <summary>The pseudo console's width in columns.</summary>
    public const short Width = 220;

    // A wrap is a line break, a few colours and a move: anything longer is not one, and is not held back.
    private const int MaxSequence = 256;

    private const char Escape = '\x1b';

    private string _held = "";
    private Skip _skip;

    private enum Wrap
    {
        None,
        Partial,
        Found,
    }

    /// <summary>What the start of the next chunk may still hold of a cell written again.</summary>
    private enum Skip
    {
        None,

        /// <summary>The move ended the last chunk: the cell starts the next one.</summary>
        Cell,

        /// <summary>The cell ended the last chunk: marks combined with it may start the next one.</summary>
        Marks,
    }

    /// <summary>
    /// The text to pass on for <paramref name="text"/>. What may be the start of a wrap at its end is held back for the
    /// next chunk only when <paramref name="moreComing"/> says the console host is still writing (asked only then): a
    /// game's last line, which ends with a line break too, is passed on whole at once.
    /// </summary>
    public string Add(string text, Func<bool> moreComing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(moreComing);
        var input = _held.Length == 0 ? text : _held + text;
        _held = "";
        var copied = 0;
        if (_skip != Skip.None && input.Length > 0)
        {
            copied = SkipWritten(input, 0, cell: _skip == Skip.Cell);
        }

        var output = new StringBuilder(input.Length);
        var at = input.IndexOf('\r', copied);
        while (at >= 0)
        {
            switch (Match(input, at, out var coloursEnd, out var end))
            {
                case Wrap.Found:
                    output.Append(input, copied, at - copied).Append(input, at + 2, coloursEnd - (at + 2));
                    _skip = Skip.Cell;
                    copied = end == input.Length ? end : SkipWritten(input, end, cell: true);
                    at = input.IndexOf('\r', copied);
                    continue;
                case Wrap.Partial when moreComing():
                    _held = input[at..];
                    return output.Append(input, copied, at - copied).ToString();
                default:
                    at = input.IndexOf('\r', at + 1);
                    break;
            }
        }

        return output.Append(input, copied, input.Length - copied).ToString();
    }

    /// <summary>What was held back, once the console is closed.</summary>
    public string Flush()
    {
        var held = _held;
        _held = "";
        _skip = Skip.None;
        return held;
    }

    /// <summary>Whether a wrap starts at <paramref name="start"/> (a carriage return), or may once more text arrives.</summary>
    private static Wrap Match(string text, int start, out int coloursEnd, out int end)
    {
        coloursEnd = end = 0;
        var i = start + 1;
        if (i == text.Length)
        {
            return Wrap.Partial;
        }

        if (text[i] != '\n')
        {
            return Wrap.None;
        }

        i++;
        while (true)
        {
            if (i == text.Length)
            {
                return Wrap.Partial;
            }

            if (text[i] != Escape)
            {
                return Wrap.None;
            }

            if (i + 1 == text.Length)
            {
                return Wrap.Partial;
            }

            if (text[i + 1] != '[')
            {
                return Wrap.None;
            }

            var parameters = i + 2;
            var final = parameters;
            while (final < text.Length && final - start <= MaxSequence && (char.IsAsciiDigit(text[final]) || text[final] == ';'))
            {
                final++;
            }

            if (final - start > MaxSequence)
            {
                return Wrap.None;
            }

            if (final == text.Length)
            {
                return Wrap.Partial;
            }

            if (text[final] == 'm')
            {
                // Colours, set again for the rest of the line.
                i = final + 1;
                continue;
            }

            if (text[final] != 'H' || !IsLastColumn(text.AsSpan(parameters, final - parameters)))
            {
                return Wrap.None;
            }

            coloursEnd = i;
            end = final + 1;
            return Wrap.Found;
        }
    }

    /// <summary><c>row;220</c>, or <c>row;219</c> for a wide character's two cells.</summary>
    private static bool IsLastColumn(ReadOnlySpan<char> parameters)
    {
        var separator = parameters.IndexOf(';');
        return separator > 0
            && int.TryParse(parameters[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(parameters[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var column)
            && column is Width or Width - 1;
    }

    /// <summary>
    /// Past the cell written again: its character (when <paramref name="cell"/>) and the marks combined with it, which
    /// the next chunk may start with when this one ends.
    /// </summary>
    private int SkipWritten(string text, int at, bool cell)
    {
        var i = at;
        if (cell)
        {
            if (char.IsControl(text[i]) || Rune.DecodeFromUtf16(text.AsSpan(i), out _, out var length) != OperationStatus.Done)
            {
                // Not a character (a sequence, a line break): nothing was written again.
                _skip = Skip.None;
                return at;
            }

            i += length;
        }

        while (i < text.Length && Rune.DecodeFromUtf16(text.AsSpan(i), out var mark, out var markLength) == OperationStatus.Done
            && Rune.GetUnicodeCategory(mark) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        {
            i += markLength;
        }

        _skip = i == text.Length ? Skip.Marks : Skip.None;
        return i;
    }
}
