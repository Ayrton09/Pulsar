using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Common;

namespace Pulsar.Infrastructure.Persistence;

internal sealed class RowLocks(PulsarDbContext db) : IRowLocks
{
    public Task LockServerAsync(ServerId server, CancellationToken cancellationToken) =>
        LockAsync(() => db.Database.ExecuteSqlAsync($"SELECT 1 FROM servers WHERE id = {server.Value} FOR UPDATE", cancellationToken));

    public Task LockUserAsync(UserId user, CancellationToken cancellationToken) =>
        LockAsync(() => db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {user.Value} FOR UPDATE", cancellationToken));

    public Task LockNodeAsync(NodeId node, CancellationToken cancellationToken) =>
        LockAsync(() => db.Database.ExecuteSqlAsync($"SELECT 1 FROM nodes WHERE id = {node.Value} FOR UPDATE", cancellationToken));

    public Task LockBackupAsync(BackupId backup, CancellationToken cancellationToken) =>
        LockAsync(() => db.Database.ExecuteSqlAsync($"SELECT 1 FROM backups WHERE id = {backup.Value} FOR UPDATE", cancellationToken));

    public Task LockServerDatabaseAsync(ServerDatabaseId database, CancellationToken cancellationToken) =>
        LockAsync(() => db.Database.ExecuteSqlAsync($"SELECT 1 FROM server_databases WHERE id = {database.Value} FOR UPDATE", cancellationToken));

    public Task LockCreationsAsync<TEntity>(CancellationToken cancellationToken)
        where TEntity : class
    {
        // An advisory lock keyed by the table's name: only those who ask for it wait.
        var key = "pulsar.creations." + (db.Model.FindEntityType(typeof(TEntity))?.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not a table of the panel's database."));
        return LockAsync(() => db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", cancellationToken));
    }

    private async Task LockAsync(Func<Task<int>> statement)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A row is locked inside a transaction.");
        }

        await statement();
    }
}
