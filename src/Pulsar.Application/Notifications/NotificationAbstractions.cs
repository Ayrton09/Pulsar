using Pulsar.Domain.Notifications;

namespace Pulsar.Application.Notifications;

/// <summary>
/// Encrypts notification secrets (Discord webhook URLs, the SMTP password) for the database with the panel's Data
/// Protection key ring, like secret server variables (docs/architecture/08, "Secrets").
/// </summary>
public interface INotificationSecretProtector
{
    byte[] Protect(string value);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The key ring that encrypted it is gone.</exception>
    string Unprotect(byte[] protectedValue);
}

/// <summary>Sends a message to one kind of channel (Discord, email). Implemented in Infrastructure.</summary>
public interface INotificationTransport
{
    NotificationKind Kind { get; }

    /// <exception cref="NotificationSendException">The message was not delivered.</exception>
    Task SendAsync(NotificationChannel channel, NotificationSettings settings, NotificationMessage message, CancellationToken cancellationToken);
}

/// <summary>A delivery that did not go through; <see cref="Retryable"/> when trying again later may work.</summary>
public sealed class NotificationSendException : Exception
{
    public NotificationSendException(string message, bool retryable, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Retryable = retryable;
        RetryAfter = retryAfter;
    }

    public NotificationSendException()
    {
    }

    public NotificationSendException(string message)
        : base(message)
    {
    }

    public NotificationSendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public bool Retryable { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary><c>FOR UPDATE SKIP LOCKED</c> over due deliveries, so two senders never take the same one.</summary>
public interface INotificationClaims
{
    /// <summary>Locks up to <paramref name="limit"/> pending deliveries due at <paramref name="now"/> (inside a transaction).</summary>
    Task<IReadOnlyList<NotificationDelivery>> LockDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

/// <summary>Wakes the notification sender when deliveries are queued (after the save that queued them).</summary>
public sealed class NotificationSignal : IDisposable
{
    private readonly SemaphoreSlim _queued = new(0, 1);

    public void Notify()
    {
        try
        {
            _queued.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _queued.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _queued.Dispose();
}
