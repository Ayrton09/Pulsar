using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Common;
using Pulsar.Domain.Notifications;

namespace Pulsar.Application.Notifications;

/// <summary>
/// Sends queued notifications: takes due deliveries under row locks (with a lease, so no transaction stays open while a
/// message is sent), sends each through its channel's transport and records the outcome; failures are retried with
/// growing delays (<see cref="NotificationDelivery.RetryDelays"/>).
/// </summary>
public sealed partial class NotificationDispatcher(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NotificationDispatcher> logger)
{
    public const int BatchSize = 20;

    /// <summary>Sends what is due now; true when more was due than one pass takes.</summary>
    public async Task<bool> SendDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var taken = new List<(NotificationDeliveryId Id, int Attempt)>();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var due = await scope.ServiceProvider.GetRequiredService<INotificationClaims>().LockDueAsync(now, BatchSize, cancellationToken);
            foreach (var delivery in due)
            {
                if (delivery.Taken(now))
                {
                    taken.Add((delivery.Id, delivery.Attempts));
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await Task.WhenAll(taken.Select(t => SendAsync(t.Id, t.Attempt, cancellationToken)));
        return taken.Count == BatchSize;
    }

    /// <summary>When the next pending delivery is due, if any.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().NotificationDeliveries.AsNoTracking()
            .Where(d => d.Status == DeliveryStatus.Pending)
            .MinAsync(d => d.NextAttemptAt, cancellationToken);
    }

    /// <summary>Deletes sent and failed deliveries older than <see cref="NotificationDelivery.KeptFor"/>.</summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var before = clock.GetUtcNow() - NotificationDelivery.KeptFor;
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().NotificationDeliveries
            .Where(d => d.Status != DeliveryStatus.Pending && d.CreatedAt < before)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <param name="attempt">The attempt this sender took (<see cref="NotificationDelivery.Attempts"/> after its take).</param>
    private async Task SendAsync(NotificationDeliveryId id, int attempt, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IPulsarDbContext>();
        var delivery = await db.NotificationDeliveries.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        var channel = delivery is null ? null : await db.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == delivery.ChannelId, cancellationToken);
        if (delivery is null || channel is null)
        {
            return; // the channel was deleted, with its deliveries
        }

        if (delivery.Status != DeliveryStatus.Pending || delivery.Attempts != attempt)
        {
            return; // read after the lease ran out: another sender took it (and may have sent it) meanwhile
        }

        try
        {
            if (!channel.IsEnabled)
            {
                throw new NotificationSendException("The channel was turned off before the message was sent.", retryable: false);
            }

            var settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? NotificationSettings.Default(clock.GetUtcNow());
            var transport = services.GetServices<INotificationTransport>().Single(t => t.Kind == channel.Kind);
            await transport.SendAsync(channel, settings, delivery.Message, cancellationToken);
            delivery.Sent(clock.GetUtcNow());
            LogSent(delivery.Id, channel.Id, delivery.Event);
        }
        catch (NotificationSendException ex)
        {
            delivery.AttemptFailed(ex.Message, ex.Retryable, ex.RetryAfter, clock.GetUtcNow());
            LogFailed(delivery.Id, channel.Id, delivery.Attempts, delivery.Status, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            delivery.AttemptFailed(ex.Message, retryable: true, retryAfter: null, clock.GetUtcNow());
            LogFailed(delivery.Id, channel.Id, delivery.Attempts, delivery.Status, ex.Message);
        }

        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Its lease ran out and another sender took it (the row version changed): that one records the outcome.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {DeliveryId} ({NotificationEvent}) sent to channel {ChannelId}")]
    private partial void LogSent(NotificationDeliveryId deliveryId, NotificationChannelId channelId, NotificationEvent notificationEvent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {DeliveryId} to channel {ChannelId} failed (attempt {Attempts}, now {Status}): {Error}")]
    private partial void LogFailed(NotificationDeliveryId deliveryId, NotificationChannelId channelId, int attempts, DeliveryStatus status, string error);
}
