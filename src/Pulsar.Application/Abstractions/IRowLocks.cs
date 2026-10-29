using Pulsar.Domain.Access;
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

    /// <summary>
    /// The node's row: its credentials (a rotation and a revocation take turns) and its allocations are checked against
    /// what is there now. Its agent's writes to the row (Hello, last seen) wait meanwhile.
    /// </summary>
    Task LockNodeAsync(NodeId node, CancellationToken cancellationToken);

    /// <summary>The backup's row: locking it (by a user) waits until the transaction ends.</summary>
    Task LockBackupAsync(BackupId backup, CancellationToken cancellationToken);

    /// <summary>A server's database's row: changes of its password, and its deletion, take turns.</summary>
    Task LockServerDatabaseAsync(ServerDatabaseId database, CancellationToken cancellationToken);

    /// <summary>
    /// A sign-in provider's settings and the accounts linked to it take turns: a link is saved only under the settings it
    /// was made with, and a change of issuer removes the links of the old one after any link in progress is saved.
    /// </summary>
    Task LockSsoProviderAsync(SsoKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Creations of <typeparamref name="TEntity"/> rows that take this lock take turns until the transaction ends (nothing
    /// else waits): a panel-wide cap is counted against what is there now.
    /// </summary>
    Task LockCreationsAsync<TEntity>(CancellationToken cancellationToken)
        where TEntity : class;
}
