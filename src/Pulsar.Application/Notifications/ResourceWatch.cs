using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;

namespace Pulsar.Application.Notifications;

/// <summary>
/// Watches running servers and nodes for the <see cref="NotificationEvent.ResourceHigh"/> notification (Pulsar 1.0.18):
/// CPU or memory at or above the settings' share at every sweep for their number of minutes. A server's share is of its
/// own limits when it has them (its CPU limit, its memory limit), else of its node's (the whole machine's CPU, the node's
/// memory). Told once, then not again until both are well below (<see cref="RecoveryMarginPercent"/>). What it saw is
/// kept in memory: a panel that restarts starts counting again.
/// </summary>
public sealed class ResourceWatch(IServiceScopeFactory scopes, TimeProvider clock)
{
    /// <summary>Usage is fine again only this far below the thresholds (no message at every wobble).</summary>
    public const int RecoveryMarginPercent = 10;

    /// <summary>A server's sample older than this belongs to one that stopped, or to a node that went away.</summary>
    public static readonly TimeSpan SampleMaximumAge = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<ServerId, ServerSample> _samples = new();
    private readonly ConcurrentDictionary<string, Watched> _watched = new(StringComparer.Ordinal);

    /// <summary>A running server's usage, as its node reported it (<paramref name="cpuPercent"/>: of the whole machine).</summary>
    /// <param name="committedBytes">What a memory limit counts (the page shows it against one); null: not reported.</param>
    public void RecordServer(ServerId server, double cpuPercent, long memoryBytes, long? committedBytes = null) =>
        _samples[server] = new ServerSample(cpuPercent, memoryBytes, committedBytes, clock.GetUtcNow());

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        foreach (var (id, sample) in _samples)
        {
            if (now - sample.At > SampleMaximumAge)
            {
                _samples.TryRemove(new KeyValuePair<ServerId, ServerSample>(id, sample));
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IPulsarDbContext>();
        var settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? NotificationSettings.Default(now);
        var connections = services.GetRequiredService<IAgentConnections>();
        var sessions = services.GetRequiredService<AgentSessionStore>();
        var notifier = services.GetRequiredService<Notifier>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var told = new List<Watched>();

        var nodes = await db.Nodes.AsNoTracking().Where(n => n.Status == NodeStatus.Active).ToListAsync(cancellationToken);
        var machines = new Dictionary<NodeId, long>();
        foreach (var node in nodes)
        {
            if (connections.Find(node.Id) is not { LastMetrics: { } metrics } live || !sessions.IsActive(live.SessionId))
            {
                continue;
            }

            machines[node.Id] = metrics.MemoryTotalBytes;
            var memory = Share(metrics.MemoryUsedBytes, metrics.MemoryTotalBytes);
            var key = $"node:{node.Id}";
            seen.Add(key);
            if (Due(key, Clamp(metrics.CpuPercent), memory, settings, now) is { } due)
            {
                await notifier.NodeResourceHighAsync(node, Clamp(metrics.CpuPercent), memory, settings.ResourceHighMinutes, cancellationToken);
                told.Add(due);
            }
        }

        var running = _samples.Keys.ToList();
        var servers = running.Count == 0 ? [] : await db.Servers.AsNoTracking().Where(s => running.Contains(s.Id)).ToListAsync(cancellationToken);
        foreach (var server in servers)
        {
            if (!_samples.TryGetValue(server.Id, out var sample) || !machines.TryGetValue(server.NodeId, out var machineMemory))
            {
                continue;
            }

            // Of its own limits when it has them: a server capped at 25 % of the machine is busy at 25 %.
            var cpu = Clamp(server.CpuLimitPercent is { } cap and > 0 ? sample.CpuPercent * 100 / cap : sample.CpuPercent);
            // Against a limit, what the limit counts (committed memory), as the page shows it; otherwise the working set.
            var limited = server.MemoryLimitMb is > 0;
            var memoryLimit = limited ? server.MemoryLimitMb!.Value * 1024L * 1024 : machineMemory;
            var memoryUsed = limited ? sample.CommittedBytes ?? sample.MemoryBytes : sample.MemoryBytes;
            var memory = Share(memoryUsed, memoryLimit);
            var key = $"server:{server.Id}";
            seen.Add(key);
            if (Due(key, cpu, memory, settings, now) is { } due)
            {
                await notifier.ServerResourceHighAsync(server, cpu, server.CpuLimitPercent is > 0, memoryUsed, memoryLimit, limited,
                    settings.ResourceHighMinutes, cancellationToken);
                told.Add(due);
            }
        }

        // Stopped servers and nodes away start again from nothing.
        foreach (var key in _watched.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _watched.TryRemove(key, out _);
        }

        if (told.Count > 0)
        {
            // Told only once its messages are saved: a sweep that fails tells them again at the next one.
            await db.SaveChangesAsync(cancellationToken);
            foreach (var watched in told)
            {
                watched.Told = true;
            }
        }
    }

    /// <summary>What to tell now: high at every sweep for long enough, and not told since it was last fine; null otherwise.</summary>
    private Watched? Due(string key, double cpu, double memory, NotificationSettings settings, DateTimeOffset now)
    {
        var watched = _watched.GetOrAdd(key, _ => new Watched());
        var high = cpu >= settings.CpuHighPercent || memory >= settings.MemoryHighPercent;
        if (!high)
        {
            watched.HighSince = null;
            if (cpu < settings.CpuHighPercent - RecoveryMarginPercent && memory < settings.MemoryHighPercent - RecoveryMarginPercent)
            {
                watched.Told = false;
            }

            return null;
        }

        watched.HighSince ??= now;
        return watched.Told || now - watched.HighSince < settings.ResourceHighAfter ? null : watched;
    }

    private static double Share(long used, long total) => total > 0 ? Clamp(used * 100.0 / total) : 0;

    // An agent's numbers are not trusted: not a number counts as nothing, the rest within 0–100.
    private static double Clamp(double percent) => double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;

    private sealed record ServerSample(double CpuPercent, long MemoryBytes, long? CommittedBytes, DateTimeOffset At);

    private sealed class Watched
    {
        public DateTimeOffset? HighSince { get; set; }

        public bool Told { get; set; }
    }
}
