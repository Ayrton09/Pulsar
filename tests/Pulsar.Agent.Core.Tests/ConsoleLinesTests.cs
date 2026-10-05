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
}
