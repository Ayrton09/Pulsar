namespace Pulsar.Contracts.Tests;

/// <summary>Names and maps a game reports are shown as they are: nothing in them may reorder or hide the text around.</summary>
public sealed class QueryTextTests
{
    [Theory]
    [InlineData("pl\U0000202Eayer", "player")]
    [InlineData("pl\U00002066ayer\U00002069", "player")]
    [InlineData("pl\U0000200Eayer", "player")] // left-to-right mark
    [InlineData("pl\U0000200Fayer", "player")] // right-to-left mark
    [InlineData("pl\U0000061Cayer", "player")] // Arabic letter mark
    [InlineData("\U0000200Fplayer", "player")]
    [InlineData("§aplayer", "player")]
    public void DirectionMarksAndOverridesAreRemoved(string text, string expected) => Assert.Equal(expected, QueryText.Clean(text));
}
