using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

/// <summary>A server's secrets hidden in its console output, which comes in chunks split anywhere.</summary>
public sealed class ConsoleMaskTests
{
    [Fact]
    public void ASecretIsMasked()
    {
        var mask = Mask("hunter22");

        Assert.Equal("LogInit: Command Line: -ServerPassword=******** -Port=7777\n",
            mask.Apply("LogInit: Command Line: -ServerPassword=hunter22 -Port=7777\n"));
    }

    [Fact]
    public void ASecretSplitAcrossChunksIsMaskedWhole()
    {
        var mask = Mask("hunter22");

        var written = mask.Apply("-ServerPassword=hun") + mask.Apply("t") + mask.Apply("er22 -Port=7777\n");

        Assert.Equal("-ServerPassword=******** -Port=7777\n", written);
    }

    [Fact]
    public void OnlyWhatMayBeginASecretIsHeldBack()
    {
        var mask = Mask("hunter22");

        Assert.Equal("Loading level", mask.Apply("Loading level"));
        Assert.Equal("Player ", mask.Apply("Player hun"));
        Assert.Equal("hungry joined\n", mask.Apply("gry joined\n"));
    }

    [Fact]
    public void ASecretThatHoldsAnotherIsMaskedWhole()
    {
        var mask = Mask("pass", "pass1234");

        Assert.Equal("a ******** b ********\n", mask.Apply("a pass1234 b pass\n"));
    }

    [Fact]
    public void EveryOccurrenceIsMasked()
    {
        var mask = Mask("s3cret!");

        Assert.Equal("?Password=********?AdminPassword=********?Port=1\n", mask.Apply("?Password=s3cret!?AdminPassword=s3cret!?Port=1\n"));
    }

    [Fact]
    public void WhatIsHeldIsFlushedMaskedWhenItCouldTellTheSecret()
    {
        var mask = Mask("hunter22");
        Assert.Equal("-ServerPassword=", mask.Apply("-ServerPassword=hunt"));

        Assert.Equal(ConsoleMask.Mask, mask.Flush());
        Assert.Equal("", mask.Flush());

        Assert.Equal("prompt ", mask.Apply("prompt h"));
        Assert.Equal("h", mask.Flush()); // one letter tells nothing
    }

    [Fact]
    public void NewSecretsWriteWhatWasHeldForTheOldOnes()
    {
        var mask = Mask("hunter22");
        Assert.Equal("x ", mask.Apply("x hu"));

        Assert.Equal("hu", mask.Use(["other-secret"]));
        Assert.Equal("other: ********\n", mask.Apply("other: other-secret\n"));
    }

    [Fact]
    public void AnEarlierRunsSecretsStayHidden()
    {
        // Late output of the last run, whose password was changed for this one, still hides the old password.
        var mask = Mask("hunter22");
        mask.Use(["new-password"]);

        Assert.Equal("old ******** new ********\n", mask.Apply("old hunter22 new new-password\n"));
    }

    [Fact]
    public void OnlyTheNewestSecretsAreKept()
    {
        var mask = Mask("secret-0");
        for (var run = 1; run <= ConsoleMask.MaximumSecrets; run++)
        {
            mask.Use([$"secret-{run}"]);
        }

        Assert.Equal("secret-0 ********\n", mask.Apply($"secret-0 secret-{ConsoleMask.MaximumSecrets}\n"));
    }

    [Fact]
    public void WithoutSecretsTextPassesThrough()
    {
        var mask = new ConsoleMask();

        Assert.Equal("anything at all", mask.Apply("anything at all"));
        Assert.Equal("", mask.Flush());
    }

    private static ConsoleMask Mask(params string[] secrets)
    {
        var mask = new ConsoleMask();
        Assert.Equal("", mask.Use(secrets));
        return mask;
    }
}
