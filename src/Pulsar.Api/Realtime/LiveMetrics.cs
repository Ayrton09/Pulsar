using System.Collections.Concurrent;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Realtime;

/// <summary>
/// The latest usage sample of every running server (memory only, docs/architecture/02): what a browser shows before the
/// next sample is pushed to it. Rebuilt within seconds after a panel restart.
/// </summary>
internal sealed class LiveMetrics(TimeProvider clock)
{
    /// <summary>Samples come every 5 s; an older one belongs to a server that stopped or a node that went away.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<ServerId, ServerMetricsView> _servers = new();

    public void Update(ServerMetricsView metrics) => _servers[new ServerId(metrics.ServerId)] = metrics;

    public ServerMetricsView? Find(ServerId server) =>
        _servers.TryGetValue(server, out var metrics) && clock.GetUtcNow() - metrics.At <= MaximumAge ? metrics : null;

    public void Remove(ServerId server) => _servers.TryRemove(server, out _);
}
