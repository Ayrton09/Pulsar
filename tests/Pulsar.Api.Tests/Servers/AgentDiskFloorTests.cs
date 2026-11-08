using Pulsar.Agent.Core.Files;
using Pulsar.Domain.Nodes;

namespace Pulsar.Api.Tests.Servers;

/// <summary>The agent keeps free on a drive what the panel does (<see cref="DiskFloor"/>): zips and restores must not eat what backups and uploads are refused for.</summary>
public sealed class AgentDiskFloorTests
{
    [Fact]
    public void TheAgentsFloorMirrorsTheNodesDiskFloor()
    {
        Assert.Equal(DiskFloor.MinFreePercent, FileArchives.MinFreePercent);
        Assert.Equal(DiskFloor.MinFreeBytes, FileArchives.MinFreeBytes);
        foreach (var (total, free, adding) in new[] { (100L << 30, 10L << 30, 5L << 30), (100L << 30, 10L << 30, 4L << 30), (10L << 30, 3L << 30, 2L << 30), (10L << 30, 3L << 30, 1L << 30), (0L, 0L, 5L) })
        {
            Assert.Equal(DiskFloor.Keeps(total, free, adding), FileArchives.KeepsFloor(total, free, adding));
        }
    }
}
