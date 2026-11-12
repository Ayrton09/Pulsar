using System.Collections.Concurrent;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Realtime;

/// <summary>
/// The latest usage sample of every running server (memory only, docs/architecture/02): what a browser shows before the
/// next sample is pushed to it. Rebuilt within seconds after a panel restart.
/// </summary>
internal sealed class LiveMetrics : IDisposable
{
    /// <summary>Samples come every 5 s; an older one belongs to a server that stopped or a node that went away.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);

    internal sealed record Sample(NodeId Node, ServerMetricsView Metrics);
    private readonly ConcurrentDictionary<ServerId, Sample> _servers = new();
    private readonly TimeProvider _clock;
    private readonly ITimer _expiration;

    public LiveMetrics(TimeProvider clock)
    {
        _clock = clock;
        // The singleton's timer must not retain the request that first resolved it.
        using var flow = ExecutionContext.SuppressFlow();
        _expiration = clock.CreateTimer(static state => ((LiveMetrics)state!).RemoveExpired(), this, MaximumAge, MaximumAge);
    }

    public void Update(NodeId node, ServerMetricsView metrics) => _servers[new ServerId(metrics.ServerId)] = new Sample(node, metrics);

    /// <summary>Internal scheduling reads are keyed by server; browser reads also require the current node.</summary>
    public ServerMetricsView? Find(ServerId server) => Find(server, node: null);

    public ServerMetricsView? Find(ServerId server, NodeId node) => Find(server, (NodeId?)node);

    private ServerMetricsView? Find(ServerId server, NodeId? node)
    {
        if (_servers.TryGetValue(server, out var metrics))
        {
            if (_clock.GetUtcNow() - metrics.Metrics.At <= MaximumAge)
            {
                return node is null || metrics.Node == node ? metrics.Metrics : null;
            }

            _servers.TryRemove(new KeyValuePair<ServerId, Sample>(server, metrics));
        }

        return null;
    }

    public void Remove(ServerId server) => _servers.TryRemove(server, out _);

    public void Dispose()
    {
        _expiration.Dispose();
        _servers.Clear();
    }

    private void RemoveExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in _servers)
        {
            if (now - entry.Value.Metrics.At > MaximumAge)
            {
                // A fresh sample may have replaced this one while it was being inspected.
                _servers.TryRemove(entry);
            }
        }
    }
}
