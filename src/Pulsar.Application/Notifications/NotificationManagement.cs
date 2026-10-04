using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Notifications;

/// <param name="WebhookUrl">Discord: the webhook address; null on an update keeps the current one.</param>
public sealed record NotificationChannelDefinition(
    string Name,
    NotificationKind Kind,
    bool Enabled,
    IReadOnlyList<NotificationEvent> Events,
    IReadOnlyList<Guid>? ServerIds,
    string Language,
    string? WebhookUrl,
    IReadOnlyList<string>? Recipients);

/// <param name="Webhook">Discord: the webhook's id, to tell webhooks apart (its token is never shown again).</param>
public sealed record NotificationChannelView(
    Guid Id,
    string Name,
    NotificationKind Kind,
    bool Enabled,
    IReadOnlyList<NotificationEvent> Events,
    IReadOnlyList<Guid>? ServerIds,
    string Language,
    IReadOnlyList<string> Recipients,
    string? Webhook,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NotificationChannelList(IReadOnlyList<NotificationChannelView> Channels);

public sealed record SmtpSettingsView(string Host, int Port, SmtpSecurity Security, string? UserName, bool HasPassword, string FromAddress, string? FromName);

public sealed record NotificationSettingsView(string? PanelUrl, int DiskLowPercent, int NodeOfflineMinutes, SmtpSettingsView? Smtp);

/// <param name="SmtpHost">Null turns email off.</param>
/// <param name="SmtpPassword">Null keeps the current password (unless <paramref name="ClearSmtpPassword"/>).</param>
public sealed record NotificationSettingsDefinition(
    string? PanelUrl,
    int DiskLowPercent,
    int NodeOfflineMinutes,
    string? SmtpHost,
    int SmtpPort,
    SmtpSecurity SmtpSecurity,
    string? SmtpUserName,
    string? SmtpPassword,
    bool ClearSmtpPassword,
    string? FromAddress,
    string? FromName);

public sealed record NotificationDeliveryView(
    Guid Id,
    Guid ChannelId,
    string ChannelName,
    NotificationEvent Event,
    string Title,
    DeliveryStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? NextAttemptAt);

public sealed record NotificationDeliveryList(IReadOnlyList<NotificationDeliveryView> Deliveries);

public sealed record NotificationTestResult(bool Sent, string? Error);

/// <summary>
/// Notification channels and settings (docs/architecture/07, "Notifications"), for holders of <c>settings.manage</c>.
/// Secrets go in encrypted and never come back out: the API shows a Discord webhook's id and whether an SMTP password is set.
/// </summary>
public sealed class NotificationManagement(
    IPulsarDbContext db,
    INotificationSecretProtector secrets,
    IServiceProvider services,
    IAuditLog audit,
    TimeProvider clock)
{
    public const int MaxDeliveriesListed = 100;

    public async Task<NotificationChannelList> ListAsync(CancellationToken cancellationToken)
    {
        var channels = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        return new NotificationChannelList([.. channels.Select(View)]);
    }

    public async Task<Result<NotificationChannelView>> CreateAsync(NotificationChannelDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (await db.NotificationChannels.CountAsync(cancellationToken) >= NotificationChannel.MaxChannels)
        {
            return NotificationErrors.TooMany;
        }

        var checkedTarget = await CheckAsync(definition, creating: true, cancellationToken);
        if (!checkedTarget.IsSuccess)
        {
            return checkedTarget.Error!;
        }

        var created = NotificationChannel.Create(definition.Kind, definition.Name, definition.Events, definition.ServerIds, definition.Language,
            definition.Enabled, checkedTarget.Value, definition.Recipients, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var channel = created.Value;
        db.NotificationChannels.Add(channel);
        audit.Record(AuditActions.Notification.ChannelCreated, metadata: new { channel.Name, kind = channel.Kind.ToString(), events = channel.Events })
            .WithTarget("notification_channel", channel.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return View(channel);
    }

    public async Task<Result<NotificationChannelView>> UpdateAsync(Guid id, NotificationChannelDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var channel = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == new NotificationChannelId(id), cancellationToken);
        if (channel is null)
        {
            return NotificationErrors.NotFound;
        }

        var checkedTarget = await CheckAsync(definition with { Kind = channel.Kind }, creating: false, cancellationToken);
        if (!checkedTarget.IsSuccess)
        {
            return checkedTarget.Error!;
        }

        var changed = channel.Change(definition.Name, definition.Events, definition.ServerIds, definition.Language, definition.Enabled,
            checkedTarget.Value, definition.Recipients, clock.GetUtcNow());
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(AuditActions.Notification.ChannelUpdated,
                metadata: new { channel.Name, enabled = channel.IsEnabled, events = channel.Events, webhookChanged = checkedTarget.Value is not null })
            .WithTarget("notification_channel", channel.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return View(channel);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var channel = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == new NotificationChannelId(id), cancellationToken);
        if (channel is null)
        {
            return NotificationErrors.NotFound;
        }

        db.NotificationChannels.Remove(channel);
        audit.Record(AuditActions.Notification.ChannelDeleted, metadata: new { channel.Name }).WithTarget("notification_channel", channel.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Sends a test message now, without queueing it, and says what happened.</summary>
    public async Task<Result<NotificationTestResult>> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        var channel = await db.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == new NotificationChannelId(id), cancellationToken);
        if (channel is null)
        {
            return NotificationErrors.NotFound;
        }

        var settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? NotificationSettings.Default(clock.GetUtcNow());
        NotificationTestResult result;
        try
        {
            var transport = services.GetServices<INotificationTransport>().Single(t => t.Kind == channel.Kind);
            await transport.SendAsync(channel, settings, NotificationTexts.Test(channel.Language, settings.PanelUrl), cancellationToken);
            result = new NotificationTestResult(true, null);
        }
        catch (NotificationSendException ex)
        {
            result = new NotificationTestResult(false, ex.Message);
        }

        audit.Record(AuditActions.Notification.Tested, result.Sent ? AuditOutcome.Success : AuditOutcome.Failure, metadata: new { channel.Name, result.Error })
            .WithTarget("notification_channel", channel.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<NotificationDeliveryList> DeliveriesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.NotificationDeliveries.AsNoTracking()
            .OrderByDescending(d => d.CreatedAt)
            .Take(MaxDeliveriesListed)
            .Join(db.NotificationChannels.AsNoTracking(), d => d.ChannelId, c => c.Id, (d, c) => new { Delivery = d, ChannelName = c.Name })
            .ToListAsync(cancellationToken);
        return new NotificationDeliveryList([.. rows.Select(r => new NotificationDeliveryView(r.Delivery.Id.Value, r.Delivery.ChannelId.Value, r.ChannelName,
            r.Delivery.Event, r.Delivery.Message.Title, r.Delivery.Status, r.Delivery.Attempts, r.Delivery.LastError, r.Delivery.CreatedAt,
            r.Delivery.SentAt, r.Delivery.Status == DeliveryStatus.Pending ? r.Delivery.NextAttemptAt : null))]);
    }

    public async Task<NotificationSettingsView> GetSettingsAsync(CancellationToken cancellationToken) =>
        View(await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? NotificationSettings.Default(clock.GetUtcNow()));

    public async Task<Result<NotificationSettingsView>> UpdateSettingsAsync(NotificationSettingsDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var now = clock.GetUtcNow();
        var settings = await db.NotificationSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = NotificationSettings.Default(now);
            db.NotificationSettings.Add(settings);
        }

        var general = settings.ChangeGeneral(definition.PanelUrl, definition.DiskLowPercent, definition.NodeOfflineMinutes, now);
        if (!general.IsSuccess)
        {
            return general.Error!;
        }

        var password = string.IsNullOrEmpty(definition.SmtpPassword) ? null : secrets.Protect(definition.SmtpPassword);
        var smtp = settings.ChangeSmtp(definition.SmtpHost, definition.SmtpPort, definition.SmtpSecurity, definition.SmtpUserName, password,
            definition.ClearSmtpPassword, definition.FromAddress, definition.FromName, now);
        if (!smtp.IsSuccess)
        {
            return smtp.Error!;
        }

        audit.Record(AuditActions.Notification.SettingsUpdated, metadata: new
        {
            settings.PanelUrl,
            settings.DiskLowPercent,
            settings.NodeOfflineMinutes,
            settings.SmtpHost,
            settings.SmtpPort,
            security = settings.SmtpSecurity.ToString(),
            settings.SmtpUserName,
            passwordChanged = password is not null || definition.ClearSmtpPassword,
            settings.FromAddress,
        });
        await db.SaveChangesAsync(cancellationToken);
        return View(settings);
    }

    /// <summary>Checks what the domain cannot (servers exist, the webhook address) and encrypts a new webhook address.</summary>
    private async Task<Result<byte[]?>> CheckAsync(NotificationChannelDefinition definition, bool creating, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(definition.Kind))
        {
            return NotificationErrors.WebhookInvalid;
        }

        if (definition.ServerIds is { Count: > 0 and <= NotificationChannel.MaxServers } wanted)
        {
            var ids = wanted.Distinct().Select(g => new ServerId(g)).ToList();
            if (await db.Servers.CountAsync(s => ids.Contains(s.Id), cancellationToken) != ids.Count)
            {
                return NotificationErrors.ServerUnknown;
            }
        }

        if (definition.Kind != NotificationKind.Discord)
        {
            return NoWebhook;
        }

        if (string.IsNullOrWhiteSpace(definition.WebhookUrl))
        {
            return creating ? NotificationErrors.WebhookInvalid : NoWebhook;
        }

        return NotificationChannel.IsDiscordWebhook(definition.WebhookUrl)
            ? secrets.Protect(definition.WebhookUrl.Trim())
            : NotificationErrors.WebhookInvalid;
    }

    // No new webhook address: none for an email channel, the current one on an update.
    private static Result<byte[]?> NoWebhook => (byte[]?)null;

    private NotificationChannelView View(NotificationChannel channel) => new(channel.Id.Value, channel.Name, channel.Kind, channel.IsEnabled,
        channel.Events, channel.ServerIds, channel.Language, channel.Recipients, WebhookId(channel), channel.CreatedAt, channel.UpdatedAt);

    private string? WebhookId(NotificationChannel channel)
    {
        if (channel.WebhookUrlProtected is null)
        {
            return null;
        }

        try
        {
            // …/webhooks/{id}/{token}: the id only.
            var segments = new Uri(secrets.Unprotect(channel.WebhookUrlProtected)).AbsolutePath.Trim('/').Split('/');
            return segments.Length >= 2 ? segments[^2] : null;
        }
        catch (CryptographicException)
        {
            return null; // the key ring that encrypted it is gone: the webhook must be set again
        }
    }

    private static NotificationSettingsView View(NotificationSettings settings) => new(settings.PanelUrl, settings.DiskLowPercent, settings.NodeOfflineMinutes,
        settings.EmailConfigured
            ? new SmtpSettingsView(settings.SmtpHost!, settings.SmtpPort, settings.SmtpSecurity, settings.SmtpUserName, settings.SmtpPasswordProtected is not null,
                settings.FromAddress!, settings.FromName)
            : null);
}
