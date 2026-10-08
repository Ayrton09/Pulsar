using Pulsar.Api.Realtime;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Realtime;

/// <summary>The panel's console copy keeps each sequence once, in order (docs/architecture/03, "Console: flow and volume").</summary>
public sealed class ConsoleMirrorTests
{
    [Fact]
    public void ABatchNeverAddsASequenceTwiceNorMovesBackwards()
    {
        var mirror = new ConsoleMirrors.Mirror(new ServerId(Guid.NewGuid()), new NodeId(Guid.NewGuid()));

        Assert.Equal([5], mirror.Add([Entry(5)]).Select(e => e.Sequence));
        Assert.Equal([6], mirror.Add([Entry(6), Entry(6)]).Select(e => e.Sequence));
        Assert.Equal([7], mirror.Add([Entry(7), Entry(6)]).Select(e => e.Sequence));
        Assert.Equal(7, mirror.Last);
        Assert.Equal([8], mirror.Add([Entry(8)]).Select(e => e.Sequence));
        Assert.Equal([5, 6, 7, 8], mirror.Since(null).Select(e => e.Sequence));
    }

    private static ConsoleEntry Entry(long sequence) => new(sequence, DateTimeOffset.UnixEpoch, $"line {sequence}\n");
}
