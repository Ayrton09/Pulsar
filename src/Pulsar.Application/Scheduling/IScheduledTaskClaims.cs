using Pulsar.Domain.Scheduling;

namespace Pulsar.Application.Scheduling;

/// <summary>Takes due tasks for one scheduler (ADR-0010). Implemented with PostgreSQL row locks.</summary>
public interface IScheduledTaskClaims
{
    /// <summary>
    /// Enabled tasks due at <paramref name="now"/>, oldest first, locked until the current transaction ends; tasks another
    /// scheduler holds are skipped. Must run inside a transaction.
    /// </summary>
    Task<IReadOnlyList<ScheduledTask>> LockDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}
