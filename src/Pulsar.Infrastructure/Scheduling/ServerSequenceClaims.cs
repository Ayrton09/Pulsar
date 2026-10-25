using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Scheduling;
using Pulsar.Domain.Scheduling;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Infrastructure.Scheduling;

/// <summary><c>FOR UPDATE SKIP LOCKED</c>, as for scheduled tasks: a sequence another panel is moving on is left to it.</summary>
internal sealed class ServerSequenceClaims(PulsarDbContext db) : IServerSequenceClaims
{
    public async Task<IReadOnlyList<ServerSequence>> LockDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Due sequences are locked inside a transaction.");
        }

        // xmin is a system column: SELECT * leaves it out, and EF needs it for the row version.
        return await db.ServerSequences
            .FromSql($"""
                SELECT s.*, s.xmin FROM server_sequences s
                WHERE s.status = 'running' AND s.wake_at <= {now}
                ORDER BY s.wake_at
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """)
            .AsTracking()
            .ToListAsync(cancellationToken);
    }
}
