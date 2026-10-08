using System.Net.Mail;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Notifications;

public enum NotificationKind
{
    /// <summary>A Discord channel webhook.</summary>
    Discord,
    Email,
}

/// <summary>What a channel can be told about (docs/architecture/02, "Notifications").</summary>
public enum NotificationEvent
{
    /// <summary>A server exited without being asked to (crashed, or gave up restarting after crashing again and again).</summary>
    ServerCrashed,

    /// <summary>A server did not start, or did not report ready in time.</summary>
    ServerStartFailed,

    /// <summary>A backup failed, or its copy to remote storage did.</summary>
    BackupFailed,

    /// <summary>A scheduled task could not do its action.</summary>
    ScheduledTaskFailed,

    /// <summary>A node lost its connection to the panel for longer than the grace period; also when it is back.</summary>
    NodeOffline,

    /// <summary>A drive of a node (game servers or backups) is nearly full.</summary>
    NodeDiskLow,
}

/// <summary>
/// Where notifications go: a Discord webhook or email addresses, the events it wants, optionally only for some servers,
/// in English or Spanish. A Discord webhook URL is a secret (whoever has it can post to the channel): the application
/// encrypts it before it reaches the channel.
/// </summary>
public sealed class NotificationChannel
{
    public const int MaxNameLength = 64;
    public const int MaxRecipients = 10;
    public const int MaxEmailLength = 254;
    public const int MaxServers = 200;
    public const int MaxChannels = 50;

    public static IReadOnlyList<string> Languages { get; } = ["en", "es"];

    private static readonly string[] s_discordHosts = ["discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com"];

    private NotificationChannel()
    {
        // For EF Core materialization.
        Name = null!;
        Language = null!;
        Events = [];
        Recipients = [];
    }

    private NotificationChannel(NotificationKind kind, DateTimeOffset now)
    {
        Id = NotificationChannelId.New();
        Kind = kind;
        CreatedAt = now;
        Name = null!;
        Language = null!;
        Events = [];
        Recipients = [];
    }

    public NotificationChannelId Id { get; private set; }

    public string Name { get; private set; }

    public NotificationKind Kind { get; private set; }

    public bool IsEnabled { get; private set; }

    public IReadOnlyList<NotificationEvent> Events { get; private set; }

    /// <summary>Server events only for these servers; null for every server. Node events are not filtered.</summary>
    public IReadOnlyList<Guid>? ServerIds { get; private set; }

    /// <summary>"en" or "es".</summary>
    public string Language { get; private set; }

    /// <summary>The Discord webhook URL, encrypted (Discord channels only).</summary>
    public byte[]? WebhookUrlProtected { get; private set; }

    /// <summary>Email addresses (email channels only).</summary>
    public IReadOnlyList<string> Recipients { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Where to post a message for <paramref name="webhook"/>: with <c>wait=true</c> (Discord answers with the message,
    /// or an error, rather than 204 before it is posted), keeping the webhook's own query (<c>thread_id</c>).
    /// </summary>
    public static string DiscordPostAddress(string webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        var url = webhook.Trim();
        return url + (url.Contains('?', StringComparison.Ordinal) ? "&wait=true" : "?wait=true");
    }

    /// <summary>
    /// Only Discord's own webhook addresses over HTTPS: the panel never posts to an address an administrator typed
    /// that could point inside the network (server-side request forgery).
    /// </summary>
    public static bool IsDiscordWebhook(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || !s_discordHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // /api/webhooks/{id}/{token}, optionally /api/v10/webhooks/...
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var start = segments is ["api", var version, ..] && version.StartsWith('v') && version.Length > 1 && version[1..].All(char.IsAsciiDigit) ? 2 : 1;
        return segments.Length == start + 3
            && segments[0] == "api"
            && segments[start] == "webhooks"
            && segments[start + 1].Length > 0 && segments[start + 1].All(char.IsAsciiDigit)
            && segments[start + 2].Length > 0 && segments[start + 2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    public static Result<NotificationChannel> Create(NotificationKind kind, string name, IEnumerable<NotificationEvent> events,
        IEnumerable<Guid>? serverIds, string language, bool enabled, byte[]? webhookUrlProtected, IEnumerable<string>? recipients, DateTimeOffset now)
    {
        if (kind == NotificationKind.Discord && webhookUrlProtected is null)
        {
            return NotificationErrors.WebhookInvalid;
        }

        var channel = new NotificationChannel(kind, now);
        var set = channel.Change(name, events, serverIds, language, enabled, webhookUrlProtected, recipients, now);
        return set.IsSuccess ? channel : set.Error!;
    }

    /// <summary>Replaces the channel's settings; a null <paramref name="webhookUrlProtected"/> keeps the current webhook.</summary>
    public Result Change(string name, IEnumerable<NotificationEvent> events, IEnumerable<Guid>? serverIds, string language, bool enabled,
        byte[]? webhookUrlProtected, IEnumerable<string>? recipients, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            return NotificationErrors.NameInvalid;
        }

        var wanted = events.Distinct().Order().ToArray();
        if (wanted.Length == 0 || wanted.Any(e => !Enum.IsDefined(e)))
        {
            return NotificationErrors.EventsRequired;
        }

        Guid[]? servers = null;
        if (serverIds is not null)
        {
            servers = [.. serverIds.Distinct()];
            if (servers.Length is 0 or > MaxServers || servers.Contains(Guid.Empty))
            {
                return NotificationErrors.ServersInvalid;
            }
        }

        if (!Languages.Contains(language, StringComparer.Ordinal))
        {
            return NotificationErrors.LanguageInvalid;
        }

        string[] addresses = [];
        if (Kind == NotificationKind.Email)
        {
            addresses = [.. (recipients ?? []).Select(r => r?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase)];
            if (addresses.Length is 0 or > MaxRecipients || !addresses.All(IsEmailAddress))
            {
                return NotificationErrors.RecipientsInvalid;
            }
        }

        Name = trimmed;
        Events = wanted;
        ServerIds = servers;
        Language = language;
        IsEnabled = enabled;
        Recipients = addresses;
        if (Kind == NotificationKind.Discord && webhookUrlProtected is not null)
        {
            WebhookUrlProtected = webhookUrlProtected;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Whether this channel wants <paramref name="notificationEvent"/> about <paramref name="serverId"/> (null: a node event).</summary>
    public bool Wants(NotificationEvent notificationEvent, Guid? serverId) =>
        IsEnabled && Events.Contains(notificationEvent) && (serverId is null || ServerIds is null || ServerIds.Contains(serverId.Value));

    /// <summary>A plain address (no display name, no list), as SMTP servers take it.</summary>
    public static bool IsEmailAddress(string? address) =>
        address is { Length: > 0 and <= MaxEmailLength }
        && !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or ';' or '<' or '>' or '"')
        && MailAddress.TryCreate(address, out var parsed)
        && parsed.Address == address
        && parsed.Host.Contains('.', StringComparison.Ordinal);
}
