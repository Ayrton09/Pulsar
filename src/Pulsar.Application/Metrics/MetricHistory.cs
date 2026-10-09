using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pulsar.Domain.Common;
using Pulsar.Domain.Metrics;

namespace Pulsar.Application.Metrics;

/// <summary>How long history is kept (configuration section <c>MetricHistory</c>).</summary>
public sealed class MetricHistoryOptions
{
    public const string SectionName = "MetricHistory";

    /// <summary>One-minute buckets: the 1-hour and 24-hour charts read them, so at least a day.</summary>
    public TimeSpan MinuteRetention { get; set; } = TimeSpan.FromDays(2);

    /// <summary>One-hour buckets: the 7-day and 30-day charts read them.</summary>
    public TimeSpan HourRetention { get; set; } = TimeSpan.FromDays(90);
}

/// <summary>The span a chart shows.</summary>
public enum MetricRange
{
    Hour,
    Day,
    Week,
    Month,
}

/// <summary>One point of a server's chart: the averages and peaks of its samples within <paramref name="At"/> and the next point.</summary>
/// <param name="PlayersAverage">Null when the game answered no query in that time.</param>
public sealed record ServerMetricPoint(DateTimeOffset At, double CpuAverage, double CpuPeak, long MemoryAverage, long MemoryPeak, double? PlayersAverage,
    int? PlayersPeak);

public sealed record NodeMetricPoint(DateTimeOffset At, double CpuAverage, double CpuPeak, long MemoryUsedAverage, long MemoryUsedPeak, long MemoryTotal);

/// <summary>A server's chart.</summary>
/// <param name="StepSeconds">Time each point covers; a missing point means no samples then (stopped, or the node away).</param>
public sealed record ServerMetricHistory(MetricRange Range, DateTimeOffset From, DateTimeOffset To, int StepSeconds, IReadOnlyList<ServerMetricPoint> Points);

/// <summary>A node's chart; see <see cref="ServerMetricHistory"/>.</summary>
public sealed record NodeMetricHistory(MetricRange Range, DateTimeOffset From, DateTimeOffset To, int StepSeconds, IReadOnlyList<NodeMetricPoint> Points);

/// <summary>Where history is kept (PostgreSQL; Infrastructure).</summary>
public interface IMetricHistoryStore
{
    /// <summary>Adds minute buckets, merging with any already written for the same minute; history of deleted servers or nodes is dropped.</summary>
    Task AddAsync(IReadOnlyCollection<ServerMetricBucket> servers, IReadOnlyCollection<NodeMetricBucket> nodes, CancellationToken cancellationToken);

    /// <summary>(Re)computes the hour buckets of the hours starting in [<paramref name="start"/>, <paramref name="until"/>) from the minute buckets.</summary>
    Task RollUpAsync(DateTimeOffset start, DateTimeOffset until, CancellationToken cancellationToken);

    Task PruneAsync(MetricResolution resolution, DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>Points <paramref name="width"/> wide over [<paramref name="start"/>, <paramref name="until"/>), from buckets of <paramref name="source"/>.</summary>
    Task<IReadOnlyList<ServerMetricPoint>> ReadServerAsync(ServerId server, MetricResolution source, TimeSpan width, DateTimeOffset start,
        DateTimeOffset until, CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeMetricPoint>> ReadNodeAsync(NodeId node, MetricResolution source, TimeSpan width, DateTimeOffset start, DateTimeOffset until,
        CancellationToken cancellationToken);
}

/// <summary>
/// Aggregated usage history (docs/architecture/02, "Metric history"). The samples agents send (servers every 5 s, nodes
/// every 15 s) are folded into one-minute buckets in memory, keyed by the panel's clock (a node's clock is not
/// trusted), and written once their minute is over; an hourly roll-up feeds the long charts. A panel stopping writes
/// the minute in progress, so at most that much is lost when it crashes.
/// </summary>
public sealed class MetricHistory(IServiceScopeFactory scopes, TimeProvider clock, IOptions<MetricHistoryOptions> options)
{
    /// <summary>
    /// Memory beyond this (64 TiB) is counted as this, and a minute keeps at most <see cref="MaxSamplesPerMinute"/>
    /// samples (agents send one every 5 or 15 s): a buggy or hostile agent sending long.MaxValue, or flooding samples,
    /// would otherwise overflow the sums (bigint) and fail every write. An hour of full minutes, each written up
    /// to three times (restarts), still fits.
    /// </summary>
    public const long MaxMemoryBytes = 1L << 46;

    public const int MaxSamplesPerMinute = 600;

    /// <summary>Buckets a failed write keeps for the next one; older ones are given up.</summary>
    private static readonly TimeSpan s_keepUnwritten = TimeSpan.FromMinutes(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<(ServerId, DateTimeOffset), ServerMetricBucket> _servers = [];
    private readonly Dictionary<(NodeId, DateTimeOffset), NodeMetricBucket> _nodes = [];
    private bool _rolledUp;

    public MetricHistoryOptions Options => options.Value;

    public void RecordServer(ServerId server, double cpuPercent, long memoryBytes, int? players)
    {
        var minute = MinuteOf(clock.GetUtcNow());
        var cpu = double.IsFinite(cpuPercent) ? Math.Clamp(cpuPercent, 0, 100) : 0;
        var memory = Math.Clamp(memoryBytes, 0, MaxMemoryBytes);
        lock (_lock)
        {
            if (!_servers.TryGetValue((server, minute), out var bucket))
            {
                bucket = new ServerMetricBucket(server, MetricResolution.Minute, minute);
                _servers.Add((server, minute), bucket);
            }
            else if (bucket.Samples >= MaxSamplesPerMinute)
            {
                return;
            }

            bucket.Samples++;
            bucket.CpuSum += cpu;
            bucket.CpuMax = Math.Max(bucket.CpuMax, cpu);
            bucket.MemorySum += memory;
            bucket.MemoryMax = Math.Max(bucket.MemoryMax, memory);
            if (players is { } count)
            {
                count = Math.Max(0, count);
                bucket.PlayerSamples++;
                bucket.PlayersSum += count;
                bucket.PlayersMax = Math.Max(bucket.PlayersMax ?? 0, count);
            }
        }
    }

    public void RecordNode(NodeId node, double cpuPercent, long memoryUsedBytes, long memoryTotalBytes)
    {
        var minute = MinuteOf(clock.GetUtcNow());
        var cpu = double.IsFinite(cpuPercent) ? Math.Clamp(cpuPercent, 0, 100) : 0;
        var used = Math.Clamp(memoryUsedBytes, 0, MaxMemoryBytes);
        lock (_lock)
        {
            if (!_nodes.TryGetValue((node, minute), out var bucket))
            {
                bucket = new NodeMetricBucket(node, MetricResolution.Minute, minute);
                _nodes.Add((node, minute), bucket);
            }
            else if (bucket.Samples >= MaxSamplesPerMinute)
            {
                return;
            }

            bucket.Samples++;
            bucket.CpuSum += cpu;
            bucket.CpuMax = Math.Max(bucket.CpuMax, cpu);
            bucket.MemoryUsedSum += used;
            bucket.MemoryUsedMax = Math.Max(bucket.MemoryUsedMax, used);
            bucket.MemoryTotal = Math.Clamp(memoryTotalBytes, 0, MaxMemoryBytes);
        }
    }

    /// <summary>Writes the buckets whose minute is over, or all of them (the panel stopping).</summary>
    /// <returns>Whether there was anything to write.</returns>
    public async Task<bool> FlushAsync(bool includeCurrentMinute, CancellationToken cancellationToken)
    {
        var current = MinuteOf(clock.GetUtcNow());
        List<ServerMetricBucket> servers;
        List<NodeMetricBucket> nodes;
        lock (_lock)
        {
            servers = [.. _servers.Values.Where(b => includeCurrentMinute || b.At < current)];
            nodes = [.. _nodes.Values.Where(b => includeCurrentMinute || b.At < current)];
            foreach (var bucket in servers)
            {
                _servers.Remove((bucket.ServerId, bucket.At));
            }

            foreach (var bucket in nodes)
            {
                _nodes.Remove((bucket.NodeId, bucket.At));
            }
        }

        if (servers.Count == 0 && nodes.Count == 0)
        {
            return false;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMetricHistoryStore>().AddAsync(servers, nodes, cancellationToken);
        }
        catch
        {
            Restore(servers, nodes, current - s_keepUnwritten);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Recomputes the hour buckets of the last hours, the current one included (it grows until it is over). The first
    /// one since the panel started reaches back to every hour whose minutes are all still kept: minutes written
    /// before the panel stopped (or crashed) and not rolled up yet then reach their hour, however long it was down
    /// (within the minute retention). An hour some of whose minutes were pruned is never recomputed from the rest.
    /// </summary>
    public async Task RollUpAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var hour = HourOf(now);
        var start = Volatile.Read(ref _rolledUp) ? hour.AddHours(-3) : HourOf(now - Options.MinuteRetention).AddHours(1);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMetricHistoryStore>().RollUpAsync(start, hour.AddHours(1), cancellationToken);
        Volatile.Write(ref _rolledUp, true);
    }

    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetricHistoryStore>();
        await store.PruneAsync(MetricResolution.Minute, now - Options.MinuteRetention, cancellationToken);
        await store.PruneAsync(MetricResolution.Hour, now - Options.HourRetention, cancellationToken);
    }

    /// <summary>A chart's span as the API names it (<c>hour</c>, <c>day</c>, <c>week</c>, <c>month</c>; any case); null otherwise.</summary>
    public static MetricRange? ParseRange(string? value) => value?.ToUpperInvariant() switch
    {
        "HOUR" => MetricRange.Hour,
        "DAY" => MetricRange.Day,
        "WEEK" => MetricRange.Week,
        "MONTH" => MetricRange.Month,
        _ => null,
    };

    /// <summary>Which buckets a chart of <paramref name="range"/> reads, and how wide its points are.</summary>
    public static (TimeSpan Span, MetricResolution Source, TimeSpan Step) Shape(MetricRange range) => range switch
    {
        MetricRange.Hour => (TimeSpan.FromHours(1), MetricResolution.Minute, TimeSpan.FromMinutes(1)),
        MetricRange.Day => (TimeSpan.FromDays(1), MetricResolution.Minute, TimeSpan.FromMinutes(5)),
        MetricRange.Week => (TimeSpan.FromDays(7), MetricResolution.Hour, TimeSpan.FromHours(1)),
        MetricRange.Month => (TimeSpan.FromDays(30), MetricResolution.Hour, TimeSpan.FromHours(4)),
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };

    /// <summary>The window of a chart ending now: from the start of its first point to the end of the current one.</summary>
    public (DateTimeOffset From, DateTimeOffset To) Window(MetricRange range)
    {
        var (span, _, step) = Shape(range);
        var now = clock.GetUtcNow();
        var to = new DateTimeOffset((now.UtcTicks / step.Ticks + 1) * step.Ticks, TimeSpan.Zero);
        return (to - span, to);
    }

    public async Task<ServerMetricHistory> ReadServerAsync(ServerId server, MetricRange range, CancellationToken cancellationToken)
    {
        var (_, source, step) = Shape(range);
        var (from, to) = Window(range);
        await using var scope = scopes.CreateAsyncScope();
        var points = await scope.ServiceProvider.GetRequiredService<IMetricHistoryStore>().ReadServerAsync(server, source, step, from, to, cancellationToken);
        return new ServerMetricHistory(range, from, to, (int)step.TotalSeconds, points);
    }

    public async Task<NodeMetricHistory> ReadNodeAsync(NodeId node, MetricRange range, CancellationToken cancellationToken)
    {
        var (_, source, step) = Shape(range);
        var (from, to) = Window(range);
        await using var scope = scopes.CreateAsyncScope();
        var points = await scope.ServiceProvider.GetRequiredService<IMetricHistoryStore>().ReadNodeAsync(node, source, step, from, to, cancellationToken);
        return new NodeMetricHistory(range, from, to, (int)step.TotalSeconds, points);
    }

    private void Restore(List<ServerMetricBucket> servers, List<NodeMetricBucket> nodes, DateTimeOffset oldest)
    {
        lock (_lock)
        {
            foreach (var bucket in servers.Where(b => b.At >= oldest))
            {
                if (_servers.TryGetValue((bucket.ServerId, bucket.At), out var newer))
                {
                    newer.Samples += bucket.Samples;
                    newer.CpuSum += bucket.CpuSum;
                    newer.CpuMax = Math.Max(newer.CpuMax, bucket.CpuMax);
                    newer.MemorySum += bucket.MemorySum;
                    newer.MemoryMax = Math.Max(newer.MemoryMax, bucket.MemoryMax);
                    newer.PlayerSamples += bucket.PlayerSamples;
                    newer.PlayersSum += bucket.PlayersSum;
                    newer.PlayersMax = bucket.PlayersMax is { } max ? Math.Max(newer.PlayersMax ?? 0, max) : newer.PlayersMax;
                }
                else
                {
                    _servers.Add((bucket.ServerId, bucket.At), bucket);
                }
            }

            foreach (var bucket in nodes.Where(b => b.At >= oldest))
            {
                if (_nodes.TryGetValue((bucket.NodeId, bucket.At), out var newer))
                {
                    newer.Samples += bucket.Samples;
                    newer.CpuSum += bucket.CpuSum;
                    newer.CpuMax = Math.Max(newer.CpuMax, bucket.CpuMax);
                    newer.MemoryUsedSum += bucket.MemoryUsedSum;
                    newer.MemoryUsedMax = Math.Max(newer.MemoryUsedMax, bucket.MemoryUsedMax);
                }
                else
                {
                    _nodes.Add((bucket.NodeId, bucket.At), bucket);
                }
            }
        }
    }

    private static DateTimeOffset MinuteOf(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

    private static DateTimeOffset HourOf(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
}
