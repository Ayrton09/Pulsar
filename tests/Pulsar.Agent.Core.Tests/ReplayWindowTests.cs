using Pulsar.Agent.Core.Commands;

namespace Pulsar.Agent.Core.Tests;

public sealed class ReplayWindowTests
{
    [Fact]
    public void EverySequenceIsAcceptedOnce()
    {
        var window = new ReplayWindow();

        Assert.True(window.TryAccept(1));
        Assert.True(window.TryAccept(2));
        Assert.False(window.TryAccept(2));
        Assert.False(window.TryAccept(1));
    }

    [Fact]
    public void ReorderedSequencesInsideTheWindowAreAccepted()
    {
        var window = new ReplayWindow();

        Assert.True(window.TryAccept(10));
        Assert.True(window.TryAccept(7));
        Assert.True(window.TryAccept(9));
        Assert.False(window.TryAccept(7));
        Assert.True(window.TryAccept(8));
    }

    [Fact]
    public void SequencesOlderThanTheWindowAreRefused()
    {
        var window = new ReplayWindow(64);

        Assert.True(window.TryAccept(100));
        Assert.True(window.TryAccept(37)); // 100 - 63: the oldest slot still inside
        Assert.False(window.TryAccept(36));
        Assert.False(window.TryAccept(1));
    }

    [Fact]
    public void SlidingForgetsOnlyTheSlotsLeftBehind()
    {
        var window = new ReplayWindow(64);
        Assert.True(window.TryAccept(5));

        // 5 and 69 share a slot; after sliding to 69, 5 is too old rather than "already seen".
        Assert.True(window.TryAccept(69));
        Assert.False(window.TryAccept(5));
        Assert.False(window.TryAccept(69));

        // A jump larger than the window clears everything, and nothing behind it is accepted twice.
        Assert.True(window.TryAccept(1_000));
        Assert.True(window.TryAccept(999));
        Assert.False(window.TryAccept(999));
        Assert.True(window.TryAccept(937));
        Assert.False(window.TryAccept(936));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSequencesAreRefused(long sequence)
    {
        Assert.False(new ReplayWindow().TryAccept(sequence));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(100)]
    public void WindowSizeMustBeAMultipleOf64(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayWindow(size));
    }
}
