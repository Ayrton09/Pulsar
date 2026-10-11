using Pulsar.Domain.Common;

namespace Pulsar.Application.Abstractions;

/// <summary>
/// Locks held until the current transaction ends (PostgreSQL <c>FOR UPDATE</c> and advisory locks; Infrastructure):
/// what is checked against the locked rows stays true until then. Each throws outside a transaction.
/// </summary>
public interface IRowLocks
{
    /// <summary>The server's row: its limits, and what hangs from it (databases, scheduled tasks), are checked against what is there now.</summary>
    Task LockServerAsync(ServerId server, CancellationToken cancellationToken);

    /// <summary>The user's row: what they own (their API keys) is counted against what is there now.</summary>
    Task LockUserAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>The backup's row: locking it (by a user) waits until the transaction ends.</summary>
    Task LockBackupAsync(BackupId backup, CancellationToken cancellationToken);

    /// <summary>A server's database's row: changes of its password, and its deletion, take turns.</summary>
    Task LockServerDatabaseAsync(ServerDatabaseId database, CancellationToken cancellationToken);

    /// <summary>
    /// Creations of <typeparamref name="TEntity"/> rows that take this lock take turns until the transaction ends (nothing
    /// else waits): a panel-wide cap is counted against what is there now.
    /// </summary>
    Task LockCreationsAsync<TEntity>(CancellationToken cancellationToken)
        where TEntity : class;
}
