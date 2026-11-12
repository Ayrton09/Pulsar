using Pulsar.Api.Realtime;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Servers;

public sealed class ConsoleMirrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyEntriesAndGapMarkersHaveABoundedRetainedHistory(bool gap)
    {
        var mirror = new ConsoleMirrors.Mirror(ServerId.New(), NodeId.New());
        var at = DateTimeOffset.UnixEpoch;
        for (var sequence = 1; sequence <= ConsoleMirrors.MaximumEntries + 2; sequence++)
        {
            mirror.Add([new ConsoleEntry(sequence, at, "", IsGap: gap)]);
        }

        var retained = mirror.Since(null);
        Assert.Equal(ConsoleMirrors.MaximumEntries, retained.Count);
        Assert.Equal(3, retained[0].Sequence);
        Assert.Equal(ConsoleMirrors.MaximumEntries + 2, mirror.Last);
        var resumed = mirror.Since(0);
        Assert.True(resumed[0].IsGap);
        Assert.Equal(2, resumed[0].Sequence);
        Assert.Equal(retained.Count + 1, resumed.Count);
        Assert.Empty(mirror.Add([new ConsoleEntry(1, at, "duplicate")]));
    }

    [Fact]
    public void CharacterLimitAndFreshSequenceDeduplicationStillApply()
    {
        var mirror = new ConsoleMirrors.Mirror(ServerId.New(), NodeId.New());
        var at = DateTimeOffset.UnixEpoch;
        var text = new string('x', ConsoleMirrors.Capacity / 2);
        mirror.Add([new ConsoleEntry(1, at, text), new ConsoleEntry(2, at, text), new ConsoleEntry(3, at, text)]);
        var retained = mirror.Since(null);
        Assert.Equal(new long[] { 2, 3 }, retained.Select(e => e.Sequence));
        Assert.Equal(ConsoleMirrors.Capacity, retained.Sum(e => e.Text.Length));
        Assert.Empty(mirror.Add([new ConsoleEntry(3, at, "duplicate")]));
    }
}
