using Pulsar.Domain.Common;

namespace Pulsar.Domain.Notifications;

public enum NotificationSeverity
{
    Error,
    Warning,

    /// <summary>Something that went wrong is fine again (a node back online).</summary>
    Recovered,
}

public sealed record NotificationField(string Name, string Value);

/// <summary>A message as the channel will show it, already in the channel's language.</summary>
public sealed record NotificationMessage(string Title, string Text, NotificationSeverity Severity, IReadOnlyList<NotificationField> Fields, string? Link);

public enum DeliveryStatus
{
    Pending,
    Sent,
    Failed,
}

/// <summary>
/// One message for one channel (an outbox row): written with the change that raised it, then sent by the panel's
/// notification sender, which retries with growing delays. Kept for a while as the channel's history.
/// </summary>
public sealed class NotificationDelivery
{
    public const int MaxSubjectLength = 128;
    public const int MaxErrorLength = 512;

    /// <summary>The waits before the second, third… attempts; after the last one the delivery fails.</summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)];

    /// <summary>The longest wait a receiver's Retry-After gets.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    /// <summary>How long a sender holds a delivery it took; another one may take it after that (the first one died).</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>The same event about the same subject is told to a channel at most once in this window.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);

    /// <summary>Sent and failed deliveries are kept this long.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    private NotificationDelivery()
    {
        // For EF Core materialization.
        Subject = null!;
        Message = null!;
    }

    private NotificationDelivery(NotificationChannelId channelId, NotificationEvent notificationEvent, string subject, NotificationMessage message,
        DateTimeOffset now)
    {
        Id = NotificationDeliveryId.New();
        ChannelId = channelId;
        Event = notificationEvent;
        Subject = subject;
        Message = message;
        Status = DeliveryStatus.Pending;
        NextAttemptAt = now;
        CreatedAt = now;
    }

    public NotificationDeliveryId Id { get; private set; }

    public NotificationChannelId ChannelId { get; private set; }

    public NotificationEvent Event { get; private set; }

    /// <summary>What it is about ("server:{id}", "node:{id}"): repeats about the same subject are dropped.</summary>
    public string Subject { get; private set; }

    public NotificationMessage Message { get; private set; }

    public DeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When a sender may take it next (pending only).</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public static NotificationDelivery Create(NotificationChannel channel, NotificationEvent notificationEvent, string subject,
        NotificationMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return new NotificationDelivery(channel.Id, notificationEvent, subject.Length > MaxSubjectLength ? subject[..MaxSubjectLength] : subject,
            message, now);
    }

    /// <summary>
    /// A sender takes it for an attempt: nobody else takes it during <see cref="Lease"/>. Like the outcomes below, only
    /// while it is pending (false otherwise): a sender whose lease ran out never changes what another one recorded.
    /// </summary>
    public bool Taken(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Pending)
        {
            return false;
        }

        Attempts++;
        NextAttemptAt = now + Lease;
        return true;
    }

    public bool Sent(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Pending)
        {
            return false;
        }

        Status = DeliveryStatus.Sent;
        SentAt = now;
        NextAttemptAt = null;
        LastError = null;
        return true;
    }

    /// <summary>
    /// The attempt failed: tried again after the next delay (or <paramref name="retryAfter"/> when the receiver asked
    /// for longer), or failed for good when <paramref name="retryable"/> is false or the attempts are used up.
    /// </summary>
    public bool AttemptFailed(string error, bool retryable, TimeSpan? retryAfter, DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Pending)
        {
            return false;
        }

        LastError = Truncate(error);
        if (!retryable || Attempts > RetryDelays.Count)
        {
            Status = DeliveryStatus.Failed;
            NextAttemptAt = null;
            return true;
        }

        var delay = RetryDelays[Math.Max(Attempts, 1) - 1];
        // The receiver's Retry-After is followed up to an hour: it cannot park a message for days.
        NextAttemptAt = now + (retryAfter > delay ? (retryAfter.Value > MaxRetryAfter ? MaxRetryAfter : retryAfter.Value) : delay);
        return true;
    }

    private static string Truncate(string error)
    {
        var text = string.IsNullOrWhiteSpace(error) ? "Unknown error." : error.Trim();
        return text.Length > MaxErrorLength ? text[..MaxErrorLength] : text;
    }
}
