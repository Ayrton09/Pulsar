using Pulsar.Agent.Windows.Processes;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Joining the lines a pseudo console wraps, on the sequences the console host wrote in GameProcessTests (Windows 11,
/// build 26200): a 220-column screen that is full, so a wrapped row ends with a line break, a move back to its last
/// column and that cell written again.
/// </summary>
public sealed class PseudoConsoleWrapsTests
{
    private const string Head = "Test server: https://example.com/";

    private static readonly string s_row = Head + Letters(PseudoConsoleWraps.Width - Head.Length);

    [Fact]
    public void AWrappedLineIsJoinedWithoutTheCellWrittenAgain()
    {
        // The row's last character is written again in column 220, and the line goes on.
        var line = s_row + "opqrst";

        var text = new PseudoConsoleWraps().Add(s_row + "\r\n\x1b[49;220H" + s_row[^1] + "opqrst\r\nnext\r\n", moreComing: () => false);

        Assert.Equal(line + "\r\nnext\r\n", text);
    }

    [Fact]
    public void ColoursSetAgainAfterTheLineBreakAreKept()
    {
        var wrapped = "\x1b[38;5;11mTest server: \x1b[38;5;14m" + s_row[13..] + "\x1b[m\r\n\x1b[38;5;14m\x1b[49;220H" + s_row[^1] + "opq\r\x1b[m\n";

        var text = new PseudoConsoleWraps().Add(wrapped, moreComing: () => false);

        Assert.Equal("\x1b[38;5;11mTest server: \x1b[38;5;14m" + s_row[13..] + "\x1b[m\x1b[38;5;14mopq\r\x1b[m\n", text);
    }

    [Fact]
    public void ALineWrappedSeveralTimesIsJoinedWhole()
    {
        var second = Letters(PseudoConsoleWraps.Width, 'A');
        var wrapped = s_row + "\r\n\x1b[49;220H" + s_row[^1] + second[1..] + "\r\n\x1b[49;220H" + second[^1] + "end\r\n";

        Assert.Equal(s_row + second[1..] + "end\r\n", new PseudoConsoleWraps().Add(wrapped, moreComing: () => false));
    }

    [Fact]
    public void ALineAsWideAsTheConsoleIsNotJoinedToTheNext()
    {
        var exact = s_row + "\x1b[50;1H\nnext\r\n";

        Assert.Equal(exact, new PseudoConsoleWraps().Add(exact, moreComing: () => false));
    }

    [Theory]
    [InlineData("\r\n\x1b[49;1Hnext")] // a move to the start of a row
    [InlineData("\r\n\x1b[49;200Hx")] // another column
    [InlineData("\r\n\x1b[220Hx")] // no row
    [InlineData("\r\n\x1b[2K\x1b[49;220Hx")] // not only colours before the move
    [InlineData("\n\x1b[49;220Hnext")] // a line feed alone
    public void OtherSequencesAfterALineBreakAreLeftAsTheyAre(string after)
    {
        var text = "first" + after + "\r\n";

        Assert.Equal(text, new PseudoConsoleWraps().Add(text, moreComing: () => false));
    }

    [Fact]
    public void AWideCharacterInTheLastTwoColumnsIsWrittenAgainFromColumn219()
    {
        var row = "say " + new string('x', 206) + "漢字テスト";

        var text = new PseudoConsoleWraps().Add(row + "\r\n\x1b[49;219Hト漢字\r\n", moreComing: () => false);

        Assert.Equal(row + "漢字\r\n", text);
    }

    [Fact]
    public void ACharacterOutsideTheBasicPlaneIsWrittenAgainWhole()
    {
        var row = new string('x', 216) + "😀😀";

        var text = new PseudoConsoleWraps().Add(row + "\r\n\x1b[49;219H😀 tail\r\n", moreComing: () => false);

        Assert.Equal(row + " tail\r\n", text);
    }

    [Fact]
    public void ACharacterWithACombiningMarkIsWrittenAgainWhole()
    {
        var row = new string('x', 219) + "é";

        var text = new PseudoConsoleWraps().Add(row + "\r\n\x1b[49;220Hétail\r\n", moreComing: () => false);

        Assert.Equal(row + "tail\r\n", text);
    }

    [Fact]
    public void AMarkCombinedWithTheCellWrittenAgainIsSkippedWhenItStartsTheNextRead()
    {
        var row = new string('x', 219) + "é";
        var wraps = new PseudoConsoleWraps();

        var text = wraps.Add(row + "\r\n\x1b[49;220He", moreComing: () => true) + wraps.Add("́taiĺ\r\n", moreComing: () => false);

        Assert.Equal(row + "taiĺ\r\n", text);
    }

    [Fact]
    public void ThePipeIsAskedOnlyWhenTheTextMayEndInAWrap()
    {
        var asked = 0;
        var wraps = new PseudoConsoleWraps();

        _ = wraps.Add("a line\r\nanother", moreComing: () => ++asked > 0);
        _ = wraps.Add(" ends\r\n\x1b[32mgreen\x1b[m\r\nnext", moreComing: () => ++asked > 0);
        Assert.Equal(0, asked);

        // A line break at the end may be where a wrap starts.
        _ = wraps.Add(" line\r\n", moreComing: () => ++asked > 0);
        _ = wraps.Add("last\r\n\x1b[4", moreComing: () => ++asked > 0);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void AWrapCutAnywhereByAReadIsJoinedWhileTheConsoleHostIsStillWriting()
    {
        var wrapped = s_row + "\r\n\x1b[38;5;14m\x1b[49;220H" + s_row[^1] + "opq\r\nnext\r\n";
        var expected = s_row + "\x1b[38;5;14mopq\r\nnext\r\n";
        for (var cut = 1; cut < wrapped.Length; cut++)
        {
            var wraps = new PseudoConsoleWraps();

            var text = wraps.Add(wrapped[..cut], moreComing: () => true) + wraps.Add(wrapped[cut..], moreComing: () => false) + wraps.Flush();

            Assert.True(expected == text, $"Cut at {cut}: {text[^40..]}");
        }
    }

    [Fact]
    public void ALineBreakTheConsoleHostWroteLastIsPassedOnAtOnce()
    {
        // A game's last line: nothing more is waiting in the pipe, so it is not held back for a wrap that will not come.
        var wraps = new PseudoConsoleWraps();

        Assert.Equal("Waiting for players...\r\n", wraps.Add("Waiting for players...\r\n", moreComing: () => false));
        Assert.Equal("", wraps.Flush());
    }

    [Fact]
    public void WhatIsHeldBackComesOutWhenTheConsoleCloses()
    {
        var wraps = new PseudoConsoleWraps();

        Assert.Equal("last", wraps.Add("last\r\n\x1b[49", moreComing: () => true));
        Assert.Equal("\r\n\x1b[49", wraps.Flush());
    }

    [Fact]
    public void ASequenceTooLongToBeAWrapIsNotHeldBack()
    {
        var text = "line\r\n" + string.Concat(Enumerable.Repeat("\x1b[1m", 80));

        Assert.Equal(text, new PseudoConsoleWraps().Add(text, moreComing: () => true));
    }

    [Fact]
    public void TheCellWrittenAgainIsSkippedWhenItStartsTheNextRead()
    {
        var wraps = new PseudoConsoleWraps();

        var text = wraps.Add(s_row + "\r\n\x1b[49;220H", moreComing: () => true) + wraps.Add(s_row[^1] + "opq\r\n", moreComing: () => false);

        Assert.Equal(s_row + "opq\r\n", text);
    }

    private static string Letters(int length, char from = 'a') =>
        string.Concat(Enumerable.Range(0, length).Select(i => (char)(from + (i % 26))));
}
