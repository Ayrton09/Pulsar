using Pulsar.Domain.Common;

namespace Pulsar.Domain.Metrics;

/// <summary>How much time one history bucket covers, in minutes (the value stored).</summary>
public enum MetricResolution : short
{
    Minute = 1,
    Hour = 60,
}

/// <summary>
/// A server's usage over one minute or one hour (docs/architecture/02, "Metric history"). Sums rather than averages,
/// so buckets written twice for the same minute (a panel restarting within it) and the hour roll-up simply add up.
/// </summary>
public sealed class ServerMetricBucket
{
    public ServerMetricBucket(ServerId serverId, MetricResolution resolution, DateTimeOffset at)
    {
        ServerId = serverId;
        Resolution = resolution;
        At = at;
    }

    public ServerId ServerId { get; private set; }

    public MetricResolution Resolution { get; private set; }

    /// <summary>Start of the bucket (UTC, on the minute or the hour).</summary>
    public DateTimeOffset At { get; private set; }

    public int Samples { get; set; }

    /// <summary>Sum of the samples' share of the node's whole CPU (0–100 each).</summary>
    public double CpuSum { get; set; }

    public double CpuMax { get; set; }

    public long MemorySum { get; set; }

    public long MemoryMax { get; set; }

    /// <summary>Samples that carried the game's answer to its query (players are averaged over these only).</summary>
    public int PlayerSamples { get; set; }

    public long PlayersSum { get; set; }

    /// <summary>Null when no sample carried a query answer.</summary>
    public int? PlayersMax { get; set; }
}

/// <summary>A node's usage over one minute or one hour; see <see cref="ServerMetricBucket"/>.</summary>
public sealed class NodeMetricBucket
{
    public NodeMetricBucket(NodeId nodeId, MetricResolution resolution, DateTimeOffset at)
    {
        NodeId = nodeId;
        Resolution = resolution;
        At = at;
    }

    public NodeId NodeId { get; private set; }

    public MetricResolution Resolution { get; private set; }

    public DateTimeOffset At { get; private set; }

    public int Samples { get; set; }

    public double CpuSum { get; set; }

    public double CpuMax { get; set; }

    public long MemoryUsedSum { get; set; }

    public long MemoryUsedMax { get; set; }

    /// <summary>The node's memory at the bucket's last sample.</summary>
    public long MemoryTotal { get; set; }
}
