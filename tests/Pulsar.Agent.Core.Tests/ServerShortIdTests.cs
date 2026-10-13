using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Tests;

public sealed class ServerShortIdTests
{
    [Theory]
    [InlineData("abcd")]
    [InlineData("a1b2c3d4e5f6")]
    public void ShortIdsAreFourToTwelveLowercaseLettersAndDigits(string shortId)
    {
        Assert.True(ServerShortId.IsValid(shortId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a1b2c3d4e5f67")]
    [InlineData("ABCD")]
    [InlineData("ab-cd")]
    [InlineData("abcd\n")] // $ also matches before a final line feed
    [InlineData("abcd\r\n")]
    public void AnythingElseIsNotAShortId(string? shortId)
    {
        Assert.False(ServerShortId.IsValid(shortId));
        if (shortId is not null)
        {
            Assert.Throws<ArgumentException>(() => ServerShortId.Validate(shortId));
        }
    }
}
