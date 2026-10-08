using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

public sealed class ConsoleLinesTests
{
    [Fact]
    public void LinesAreSplitAndCleanedOfEscapes()
    {
        var lines = new ConsoleLines();

        Assert.Empty(lines.Add("\u001b[32mLoading"));
        Assert.Equal(["Loading map", "Done"], lines.Add(" map\u001b[0m\r\nDone\r\nhalf"));
        Assert.Equal(["half a line"], lines.Add(" a line\n"));
    }

    [Fact]
    public void MovingToTheStartOfARowEndsTheLine()
    {
        // A pseudo console repaints several lines written at once with cursor moves instead of line breaks (Palworld).
        var lines = new ConsoleLines();

        Assert.Equal(["[LOG] Save", "[LOG] Shutdown 1 Pulsar"], lines.Add("[LOG] Save\u001b[5;1H[LOG] Shutdown 1 Pulsar\u001b[6;1H"));
        Assert.Equal(["name,playeruid,steamid"], lines.Add("\u001b[Hname,playeruid,steamid\r\n")); // home on an empty line adds nothing
        Assert.Equal(["Progress 50%"], lines.Add("Progress \u001b[1;10H50%\r\n")); // a move inside the row is not a new line
    }

    [Fact]
    public void AMoveSplitAcrossChunksStillEndsTheLine()
    {
        var lines = new ConsoleLines();

        Assert.Empty(lines.Add("first\u001b[7;"));
        Assert.Equal(["first", "second"], lines.Add("1Hsecond\n"));
    }

    [Fact]
    public void AChunkOfManyLinesIsSplitInLinearTime()
    {
        // Each line used to copy and scan everything after it: a large chunk took seconds of CPU.
        var lines = new ConsoleLines();
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 40_000; i++)
        {
            text.Append(i % 3 == 0 ? $"\u001b[32mline {i}\u001b[m\r\n" : i % 3 == 1 ? $"line {i}\u001b[{i % 50};1H" : $"line {i}\n");
        }

        text.Append("unfinished");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var added = lines.Add(text.ToString());

        watch.Stop();
        Assert.Equal(40_000, added.Count);
        Assert.Equal("line 0", added[0]);
        Assert.Equal("line 39999", added[^1]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed}.");
        Assert.Equal(["unfinished line"], lines.Add(" line\n"));
    }
}
