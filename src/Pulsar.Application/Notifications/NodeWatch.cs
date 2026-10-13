using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Common;
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
        List<NodeId> nodes;
        NotificationSettings settings;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? NotificationSettings.Default(clock.GetUtcNow());
            nodes = await db.Nodes.AsNoTracking().Where(n => n.Status == NodeStatus.Active).Select(n => n.Id).ToListAsync(cancellationToken);
        }

        // One node at a time: a node an agent's connection changes meanwhile is looked at again at the next sweep,
        // without holding back the others.
        foreach (var id in nodes)
        {
            await CheckAsync(id, settings, cancellationToken);
        }
    }

    private async Task CheckAsync(NodeId id, NotificationSettings settings, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IPulsarDbContext>();
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id && n.Status == NodeStatus.Active, cancellationToken);
        if (node is null)
        {
            return;
        }

        var notifier = services.GetRequiredService<Notifier>();
        var now = clock.GetUtcNow();
        // A connection whose session ended (revocation, key rotation) is about to close or be replaced: not connected.
        var connection = services.GetRequiredService<IAgentConnections>().Find(node.Id) is { } live
            && services.GetRequiredService<AgentSessionStore>().IsActive(live.SessionId) ? live : null;
        if (connection is null)
        {
            // Approving or enabling the node (UpdatedAt) restarts the grace too: its agent was turned away until then, so
            // a node that waited days for approval is not told offline the moment it is approved.
            var lastSeen = node.LastSeenAt ?? node.CreatedAt;
            var graceFrom = new[] { lastSeen, node.UpdatedAt, _startedAt }.Max();
            if (node.OfflineNotifiedAt is not null || now - graceFrom < settings.NodeOfflineAfter)
            {
                return;
            }

            node.OfflineNotified(now);
            await notifier.NodeOfflineAsync(node, lastSeen, cancellationToken);
        }
        else
        {
            var changed = false;
            if (node.BackOnline())
            {
                await notifier.NodeOnlineAsync(node, cancellationToken);
                changed = true;
            }

            if (connection.LastMetrics?.Disks is { Count: > 0 } disks)
            {
                var low = disks.Where(d => d.TotalBytes > 0 && d.FreeBytes * 100.0 / d.TotalBytes < settings.DiskLowPercent).ToList();
                if (low.Count > 0 && node.DiskLowNotified(now))
                {
                    await notifier.NodeDiskLowAsync(node, [.. low.Select(d => (d.Root, d.FreeBytes, d.TotalBytes))], cancellationToken);
                    changed = true;
                }
                else if (low.Count == 0 && node.DiskLowNotifiedAt is not null
                    && disks.All(d => d.TotalBytes <= 0 || d.FreeBytes * 100.0 / d.TotalBytes >= settings.DiskLowPercent + RecoveryMarginPercent))
                {
                    node.DiskSpaceRecovered();
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent's connection changed the node meanwhile: the next sweep sees it again.
        }
    }
}
