using Pulsar.Contracts;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

public static class NodeDisks
{
    /// <summary>
    /// The drive the node uses for <paramref name="use"/> (<see cref="DiskUses"/>) that <paramref name="adding"/> more
    /// bytes would take below the <see cref="DiskFloor"/>, as its last metrics tell; null when there is room, or when the
    /// agent did not say (an older agent, no metrics yet).
    /// </summary>
    public static DiskUsage? BelowFloor(this AgentConnectionInfo connection, string use, long adding = 0)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.LastMetrics?.Disks?.FirstOrDefault(d => d.Uses.Contains(use, StringComparer.Ordinal)
            && !DiskFloor.Keeps(d.TotalBytes, d.FreeBytes, adding));
    }
}
