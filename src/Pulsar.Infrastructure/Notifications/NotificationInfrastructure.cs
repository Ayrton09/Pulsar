using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Notifications;
using Pulsar.Domain.Notifications;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Infrastructure.Notifications;

/// <summary>Notification secrets encrypted with the panel's Data Protection key ring (ADR-0011).</summary>
internal sealed class DataProtectionNotificationSecretProtector(IDataProtectionProvider provider) : INotificationSecretProtector
{
    private const string Purpose = "Pulsar.Notifications.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public byte[] Protect(string value) => _protector.Protect(Encoding.UTF8.GetBytes(value));

    public string Unprotect(byte[] protectedValue) => Encoding.UTF8.GetString(_protector.Unprotect(protectedValue));
}

/// <summary><c>FOR UPDATE SKIP LOCKED</c>: a delivery another sender is taking is left to it.</summary>
internal sealed class NotificationClaims(PulsarDbContext db) : INotificationClaims
{
    public async Task<IReadOnlyList<NotificationDelivery>> LockDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Due deliveries are locked inside a transaction.");
        }

        // xmin is a system column: SELECT * leaves it out, and EF needs it for the row version.
        return await db.NotificationDeliveries
            .FromSql($"""
                SELECT d.*, d.xmin FROM notification_deliveries d
                WHERE d.status = 'pending' AND d.next_attempt_at <= {now}
                ORDER BY d.next_attempt_at
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """)
            .AsTracking()
            .ToListAsync(cancellationToken);
    }
}
