using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;

namespace Pulsar.Application.Notifications;

/// <summary>
/// Watches the nodes for the node notifications: offline longer than the grace period (and back), and drives nearly
/// full. A sweep rather than a reaction to disconnections: reconnections within the grace period say nothing, and a node
/// that never comes back after a panel restart is still noticed. Each node is told once until it recovers.
/// </summary>
public sealed class NodeWatch(IServiceScopeFactory scopes, TimeProvider clock)
{
    /// <summary>A low drive is fine again only with this much more free space (no flapping around the threshold).</summary>
    public const int RecoveryMarginPercent = 2;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // Nodes disconnected while the panel was down get the whole grace period from the panel's start to come back.
    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IPulsarDbContext>();
        var connections = services.GetRequiredService<IAgentConnections>();
        var notifier = services.GetRequiredService<Notifier>();
        var now = clock.GetUtcNow();
        var settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? NotificationSettings.Default(now);

        foreach (var node in await db.Nodes.Where(n => n.Status == NodeStatus.Active).ToListAsync(cancellationToken))
        {
            var connection = connections.Find(node.Id);
            if (connection is null)
            {
                var lastSeen = node.LastSeenAt ?? node.CreatedAt;
                var graceFrom = lastSeen > _startedAt ? lastSeen : _startedAt;
                if (node.OfflineNotifiedAt is null && now - graceFrom >= settings.NodeOfflineAfter && node.OfflineNotified(now))
                {
                    await notifier.NodeOfflineAsync(node, lastSeen, cancellationToken);
                }

                continue;
            }

            if (node.BackOnline())
            {
                await notifier.NodeOnlineAsync(node, cancellationToken);
            }

            if (connection.LastMetrics?.Disks is { Count: > 0 } disks)
            {
                var low = disks.Where(d => d.TotalBytes > 0 && d.FreeBytes * 100.0 / d.TotalBytes < settings.DiskLowPercent).ToList();
                if (low.Count > 0)
                {
                    if (node.DiskLowNotified(now))
                    {
                        await notifier.NodeDiskLowAsync(node, [.. low.Select(d => (d.Root, d.FreeBytes, d.TotalBytes))], cancellationToken);
                    }
                }
                else if (disks.All(d => d.TotalBytes <= 0 || d.FreeBytes * 100.0 / d.TotalBytes >= settings.DiskLowPercent + RecoveryMarginPercent))
                {
                    node.DiskSpaceRecovered();
                }
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // An agent's connection changed a node meanwhile: the next sweep sees it again.
        }
    }
}
