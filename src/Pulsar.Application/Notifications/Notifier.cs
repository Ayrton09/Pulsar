using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Notifications;

/// <summary>
/// Queues notifications for the channels that want them (docs/architecture/02, "Notifications"). The deliveries are
/// added to the caller's unit of work: they are saved, or dropped, with the change that raised them, and the sender
/// takes them after the save. Repeats of an event about the same subject within <see cref="NotificationDelivery.DuplicateWindow"/>
/// are dropped (a server crashing every minute is one message).
/// </summary>
public sealed class Notifier(IPulsarDbContext db, TimeProvider clock)
{
    public Task ServerCrashedAsync(Server server, int? exitCode, bool crashLoop, CancellationToken cancellationToken, bool memoryLimit = false) =>
        ForServerAsync(server, NotificationEvent.ServerCrashed, crashLoop ? "crash_loop" : memoryLimit ? "memory_limit" : null,
            (language, facts, link) => NotificationTexts.ServerCrashed(language, facts, exitCode, crashLoop, link, memoryLimit), cancellationToken);

    public Task ServerStartFailedAsync(Server server, bool notReady, string? detail, CancellationToken cancellationToken) =>
        ForServerAsync(server, NotificationEvent.ServerStartFailed, null,
            (language, facts, link) => NotificationTexts.ServerStartFailed(language, facts, notReady, detail, link), cancellationToken);

    public Task BackupFailedAsync(Server server, string backupName, string? error, bool remoteCopy, CancellationToken cancellationToken) =>
        ForServerAsync(server, NotificationEvent.BackupFailed, remoteCopy ? "remote" : null,
            (language, facts, link) => NotificationTexts.BackupFailed(language, facts, backupName, error, remoteCopy, link), cancellationToken);

    public Task ScheduledTaskFailedAsync(Server server, string taskName, string? error, CancellationToken cancellationToken) =>
        ForServerAsync(server, NotificationEvent.ScheduledTaskFailed, taskName,
            (language, facts, link) => NotificationTexts.ScheduledTaskFailed(language, facts, taskName, error, link), cancellationToken);

    /// <param name="installs">The server installs it by itself (told in the message).</param>
    public Task GameUpdateAvailableAsync(Server server, string? installed, string latest, bool installs, CancellationToken cancellationToken) =>
        ForServerAsync(server, NotificationEvent.GameUpdateAvailable, $"game_update:{latest}",
            (language, facts, link) => NotificationTexts.GameUpdateAvailable(language, facts, installed, latest, installs, link), cancellationToken);

    /// <summary>Told to the channels that are told of updates (the same event).</summary>
    public Task GameUpdateInstalledAsync(Server server, string? latest, CancellationToken cancellationToken) =>
        ForServerAsync(server, NotificationEvent.GameUpdateAvailable, $"game_update_installed:{latest}",
            (language, facts, link) => NotificationTexts.GameUpdateInstalled(language, facts, latest, link), cancellationToken);

    public Task GameUpdateFailedAsync(Server server, string? latest, string? error, CancellationToken cancellationToken) =>
        ForServerAsync(server, NotificationEvent.GameUpdateFailed, $"game_update:{latest}",
            (language, facts, link) => NotificationTexts.GameUpdateFailed(language, facts, latest, error, link), cancellationToken);

    public Task NodeOfflineAsync(Node node, DateTimeOffset lastSeen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        return QueueAsync(NotificationEvent.NodeOffline, $"node:{node.Id}:offline", null, $"/nodes/{node.Id}",
            (language, link) => Task.FromResult(NotificationTexts.NodeOffline(language, node.Name, lastSeen, link)), cancellationToken);
    }

    /// <summary>Told to the channels that were told it went offline (the same event).</summary>
    public Task NodeOnlineAsync(Node node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        return QueueAsync(NotificationEvent.NodeOffline, $"node:{node.Id}:online", null, $"/nodes/{node.Id}",
            (language, link) => Task.FromResult(NotificationTexts.NodeOnline(language, node.Name, link)), cancellationToken);
    }

    public Task NodeDiskLowAsync(Node node, IReadOnlyList<(string Root, long Free, long Total)> drives, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        return QueueAsync(NotificationEvent.NodeDiskLow, $"node:{node.Id}:disk", null, $"/nodes/{node.Id}",
            (language, link) => Task.FromResult(NotificationTexts.NodeDiskLow(language, node.Name, drives, link)), cancellationToken);
    }

    /// <summary>The public IP certificate could not be renewed (<paramref name="expiresAt"/>: the one still served, if any).</summary>
    public Task PublicIpCertificateFailedAsync(string ip, string error, DateTimeOffset? expiresAt, CancellationToken cancellationToken) =>
        QueueAsync(NotificationEvent.PublicIpCertificate, "panel:public_ip_certificate:failed", null, "/nodes",
            (language, link) => Task.FromResult(NotificationTexts.PublicIpCertificateFailed(language, ip, error, expiresAt, link)), cancellationToken);

    /// <summary>The certificate served now expires soon and no new one came yet: told once per certificate.</summary>
    public Task PublicIpCertificateExpiringAsync(string ip, string sha256, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        QueueAsync(NotificationEvent.PublicIpCertificate, $"panel:public_ip_certificate:expiring:{sha256}", null, "/nodes",
            (language, link) => Task.FromResult(NotificationTexts.PublicIpCertificateExpiring(language, ip, expiresAt, link)), cancellationToken);

    /// <summary>A certificate came after failures that were notified.</summary>
    public Task PublicIpCertificateRenewedAsync(string ip, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        QueueAsync(NotificationEvent.PublicIpCertificate, "panel:public_ip_certificate:renewed", null, "/nodes",
            (language, link) => Task.FromResult(NotificationTexts.PublicIpCertificateRenewed(language, ip, expiresAt, link)), cancellationToken);

    private async Task ForServerAsync(Server server, NotificationEvent notificationEvent, string? qualifier,
        Func<string, ServerFacts, string?, NotificationMessage> build, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ServerFacts? facts = null;
        var subject = qualifier is null ? $"server:{server.Id}" : $"server:{server.Id}:{qualifier}";
        await QueueAsync(notificationEvent, subject, server.Id.Value, $"/servers/{server.Id}", async (language, link) =>
        {
            facts ??= await FactsAsync(server, cancellationToken);
            return build(language, facts, link);
        }, cancellationToken);
    }

    private async Task<ServerFacts> FactsAsync(Server server, CancellationToken cancellationToken)
    {
        var game = await db.GameTemplates.AsNoTracking().Where(t => t.Id == server.TemplateId).Select(t => t.Name).SingleOrDefaultAsync(cancellationToken);
        var node = await db.Nodes.AsNoTracking().Where(n => n.Id == server.NodeId).Select(n => n.Name).SingleOrDefaultAsync(cancellationToken);
        return new ServerFacts(server.Id.Value, server.Name, game ?? "?", node ?? "?");
    }

    private async Task QueueAsync(NotificationEvent notificationEvent, string subject, Guid? serverId, string linkPath,
        Func<string, string?, Task<NotificationMessage>> build, CancellationToken cancellationToken)
    {
        var channels = (await db.NotificationChannels.AsNoTracking().Where(c => c.IsEnabled).ToListAsync(cancellationToken))
            .Where(c => c.Wants(notificationEvent, serverId))
            .ToList();
        if (channels.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var since = now - NotificationDelivery.DuplicateWindow;
        var ids = channels.Select(c => c.Id).ToList();
        var told = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => ids.Contains(d.ChannelId) && d.Event == notificationEvent && d.Subject == subject && d.CreatedAt >= since)
            .Select(d => d.ChannelId)
            .ToListAsync(cancellationToken);
        told.AddRange(db.NotificationDeliveries.Local.Where(d => d.Event == notificationEvent && d.Subject == subject).Select(d => d.ChannelId));

        var panelUrl = await db.NotificationSettings.AsNoTracking().Select(s => s.PanelUrl).SingleOrDefaultAsync(cancellationToken);
        var link = panelUrl is null ? null : panelUrl + linkPath;
        foreach (var channel in channels.Where(c => !told.Contains(c.Id)))
        {
            db.NotificationDeliveries.Add(NotificationDelivery.Create(channel, notificationEvent, subject, await build(channel.Language, link), now));
        }
    }
}
