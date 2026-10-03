using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Scheduling;
using Pulsar.Domain.Scheduling;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Infrastructure.Scheduling;

/// <summary>
/// <c>FOR UPDATE SKIP LOCKED</c> (ADR-0010): a task another panel is taking is left to it, so no task runs twice even if
/// the panel is ever scaled out.
/// </summary>
internal sealed class ScheduledTaskClaims(PulsarDbContext db) : IScheduledTaskClaims
{
    public async Task<IReadOnlyList<ScheduledTask>> LockDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Due tasks are locked inside a transaction.");
        }

        // xmin is a system column: SELECT * leaves it out, and EF needs it for the row version.
        return await db.ScheduledTasks
            .FromSql($"""
                SELECT t.*, t.xmin FROM scheduled_tasks t
                WHERE t.is_enabled AND t.next_run_at <= {now}
                ORDER BY t.next_run_at
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """)
            .AsTracking()
            .ToListAsync(cancellationToken);
    }
}
