using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Databases;

/// <param name="Host">The host's name in the panel.</param>
/// <param name="Address">Where the game server connects (the host's address for servers).</param>
/// <param name="UserHost">Where its user may connect from.</param>
public sealed record ServerDatabaseView(
    Guid Id,
    string Name,
    string UserName,
    Guid HostId,
    string Host,
    string Address,
    int Port,
    string UserHost,
    DateTimeOffset CreatedAt,
    DateTimeOffset PasswordChangedAt);

/// <param name="Hosts">The hosts the server's node may use (for creating one).</param>
public sealed record ServerDatabaseList(int Limit, IReadOnlyList<ServerDatabaseView> Databases, IReadOnlyList<DatabaseHostChoice> Hosts);

public sealed record DatabaseHostChoice(Guid Id, string Name);

/// <summary>A database with its password: when it was created, its password rotated, or asked for.</summary>
public sealed record ServerDatabaseCredentials(ServerDatabaseView Database, string Password);

/// <summary>
/// A server's MySQL/MariaDB databases (Phase 13): <c>server.databases.view</c> lists them, <c>server.databases.manage</c>
/// creates, deletes and rotates them and reads their passwords. Each one is created on its host first and kept only
/// when that worked; the server's limit is checked with its row locked, so two requests cannot both take the last one.
/// </summary>
public sealed partial class ServerDatabases(
    IPulsarDbContext db,
    DatabaseHosts hosts,
    IDatabaseSecretProtector secrets,
    IDatabaseHostClient client,
    IRowLocks locks,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<ServerDatabases> logger)
{
    public async Task<Result<ServerDatabaseList>> ListAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serverId, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var all = await db.DatabaseHosts.AsNoTracking().OrderBy(h => h.Name).ToListAsync(cancellationToken);
        var databases = await db.ServerDatabases.AsNoTracking().Where(d => d.ServerId == serverId).OrderBy(d => d.Name).ToListAsync(cancellationToken);
        return new ServerDatabaseList(server.DatabaseLimit, [.. databases.Select(d => View(d, all.Single(h => h.Id == d.HostId)))],
            [.. all.Where(h => h.Serves(server.NodeId)).Select(h => new DatabaseHostChoice(h.Id.Value, h.Name))]);
    }

    public async Task<Result<ServerDatabaseCredentials>> CreateAsync(ServerId serverId, Guid hostId, string name, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serverId, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (server.ActiveOperationType == OperationType.ServerDelete)
        {
            return ServerErrors.Busy;
        }

        var host = await db.DatabaseHosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == new DatabaseHostId(hostId), cancellationToken);
        if (host is null)
        {
            return DatabaseErrors.HostNotFound;
        }

        if (!host.Serves(server.NodeId))
        {
            return DatabaseErrors.HostNotAllowed;
        }

        if (hosts.Connection(host) is not { } connection)
        {
            return DatabaseErrors.HostPasswordUnreadable;
        }

        var password = ServerDatabase.NewPassword();
        var created = ServerDatabase.Create(server.Id, server.ShortId, host, name, secrets.Protect(password), clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var database = created.Value;
        if (await ReserveAsync(server.Id, database, cancellationToken) is { } refused)
        {
            return refused;
        }

        // Reserved: the host now. Any failure (the request given up included) gives the place back.
        try
        {
            await client.CreateAsync(connection, database.Name, database.UserHost, password, cancellationToken);
        }
        catch (DatabaseHostException ex)
        {
            await ReleaseAsync(database);
            return HostFailed(host, ex);
        }
        catch
        {
            await ReleaseAsync(database);
            throw;
        }

        // The server's deletion may have completed meanwhile: it dropped its databases (this one before the host made it)
        // with the server's row locked, so under that lock the row still being here means the server is too.
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await locks.LockServerAsync(server.Id, CancellationToken.None);
        if (!await db.ServerDatabases.AsNoTracking().AnyAsync(d => d.Id == database.Id, CancellationToken.None))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.Entry(database).State = EntityState.Detached;
            await DropOrphanAsync(connection, database, host);
            return ServerErrors.NotFound;
        }

        audit.Record(AuditActions.Server.DatabaseCreated, metadata: new { database.Name, Host = host.Name }).WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return new ServerDatabaseCredentials(View(database, host), password);
    }

    /// <summary>Audited (who read which database's password, never the password).</summary>
    public async Task<Result<ServerDatabaseCredentials>> PasswordAsync(ServerId serverId, Guid databaseId, CancellationToken cancellationToken)
    {
        var (database, host) = await FindAsync(serverId, databaseId, tracking: false, cancellationToken);
        if (database is null || host is null)
        {
            return DatabaseErrors.NotFound;
        }

        string password;
        try
        {
            password = secrets.Unprotect(database.PasswordProtected);
        }
        catch (CryptographicException)
        {
            return DatabaseErrors.PasswordUnreadable;
        }

        audit.Record(AuditActions.Server.DatabasePasswordViewed, metadata: new { database.Name, Host = host.Name })
            .WithServer(serverId, await NodeOfAsync(serverId, cancellationToken));
        await db.SaveChangesAsync(CancellationToken.None);
        return new ServerDatabaseCredentials(View(database, host), password);
    }

    /// <summary>
    /// Rotations of a database take turns (its row locked from the host's change to the save), so the host and the
    /// panel always end with the same password; once asked, the host's change is waited for even when the request is
    /// given up.
    /// </summary>
    public async Task<Result<ServerDatabaseCredentials>> RotatePasswordAsync(ServerId serverId, Guid databaseId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockServerDatabaseAsync(new ServerDatabaseId(databaseId), cancellationToken);
        var (database, host) = await FindAsync(serverId, databaseId, tracking: true, cancellationToken);
        if (database is null || host is null)
        {
            return DatabaseErrors.NotFound;
        }

        if (hosts.Connection(host) is not { } connection)
        {
            return DatabaseErrors.HostPasswordUnreadable;
        }

        var password = ServerDatabase.NewPassword();
        try
        {
            await client.SetPasswordAsync(connection, database.Name, database.UserHost, password, CancellationToken.None);
        }
        catch (DatabaseHostException ex)
        {
            return HostFailed(host, ex);
        }

        database.PasswordChanged(secrets.Protect(password), clock.GetUtcNow());
        audit.Record(AuditActions.Server.DatabasePasswordRotated, metadata: new { database.Name, Host = host.Name })
            .WithServer(serverId, await NodeOfAsync(serverId, CancellationToken.None));
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return new ServerDatabaseCredentials(View(database, host), password);
    }

    /// <summary>
    /// Drops it on its host, then forgets it; a host that cannot be reached keeps it (try again later). A rotation of
    /// its password under way is waited for.
    /// </summary>
    public async Task<Result> DeleteAsync(ServerId serverId, Guid databaseId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockServerDatabaseAsync(new ServerDatabaseId(databaseId), cancellationToken);
        var (database, host) = await FindAsync(serverId, databaseId, tracking: true, cancellationToken);
        if (database is null || host is null)
        {
            return DatabaseErrors.NotFound;
        }

        if (hosts.Connection(host) is not { } connection)
        {
            return DatabaseErrors.HostPasswordUnreadable;
        }

        try
        {
            await client.DropAsync(connection, database.Name, database.UserHost, cancellationToken);
        }
        catch (DatabaseHostException ex)
        {
            return HostFailed(host, ex);
        }

        db.ServerDatabases.Remove(database);
        audit.Record(AuditActions.Server.DatabaseDeleted, metadata: new { database.Name, Host = host.Name, dropped = true })
            .WithServer(serverId, await NodeOfAsync(serverId, CancellationToken.None));
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return Result.Success();
    }

    /// <summary>
    /// Drops every database of a server its node has just deleted, before the server's row goes (saved by the caller,
    /// with it). A host that cannot be reached does not hold the deletion up: Pulsar forgets the database anyway and the
    /// audit log names what was left on the host, to remove by hand. A host that failed once is not tried again for the
    /// server's other databases on it: each attempt may wait for a connection timeout, and the node's events wait with
    /// it. Dropping is idempotent, so a retried save is fine. Inside the caller's transaction: the server's row stays
    /// locked until it is gone, so a database whose host was still creating it is dropped again by its creation.
    /// </summary>
    public async Task DropAllAsync(Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        await locks.LockServerAsync(server.Id, cancellationToken);
        var databases = await db.ServerDatabases.Where(d => d.ServerId == server.Id).ToListAsync(cancellationToken);
        var failed = new Dictionary<DatabaseHostId, string>();
        foreach (var database in databases)
        {
            var host = await db.DatabaseHosts.AsNoTracking().SingleAsync(h => h.Id == database.HostId, cancellationToken);
            if (!failed.TryGetValue(host.Id, out var leftBecause))
            {
                if (hosts.Connection(host) is not { } connection)
                {
                    leftBecause = failed[host.Id] = DatabaseErrors.HostPasswordUnreadable.Message;
                }
                else
                {
                    try
                    {
                        await client.DropAsync(connection, database.Name, database.UserHost, cancellationToken);
                    }
                    catch (DatabaseHostException ex)
                    {
                        leftBecause = failed[host.Id] = ex.Message;
                    }
                }
            }

            if (leftBecause is not null)
            {
                LogDatabaseLeft(database.Name, host.Name, leftBecause);
            }

            db.ServerDatabases.Remove(database);
            audit.Record(AuditActions.Server.DatabaseDeleted, metadata: new { database.Name, Host = host.Name, dropped = leftBecause is null, leftBecause })
                .WithServer(server.Id, server.NodeId);
        }
    }

    /// <summary>Adds the row with the server's row locked: its limit and the name are checked against what is there now.</summary>
    private async Task<DomainError?> ReserveAsync(ServerId serverId, ServerDatabase database, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockServerAsync(serverId, cancellationToken);
        var server = await db.Servers.AsNoTracking().Where(s => s.Id == serverId).Select(s => new { s.DatabaseLimit, s.ActiveOperationType })
            .SingleOrDefaultAsync(cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        // A server being deleted gets none: its deletion drops the ones it has when the node confirms it.
        if (server.ActiveOperationType == OperationType.ServerDelete)
        {
            return ServerErrors.Busy;
        }
        var existing = await db.ServerDatabases.Where(d => d.ServerId == serverId).Select(d => d.Name).ToListAsync(cancellationToken);
        if (existing.Contains(database.Name, StringComparer.Ordinal))
        {
            return DatabaseErrors.NameTaken;
        }

        if (existing.Count >= server.DatabaseLimit)
        {
            return DatabaseErrors.LimitReached;
        }

        db.ServerDatabases.Add(database);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    /// <summary>Drops a database its server's deletion left to its creation; one the host keeps is logged, to remove by hand.</summary>
    private async Task DropOrphanAsync(DatabaseHostConnection connection, ServerDatabase database, DatabaseHost host)
    {
        try
        {
            await client.DropAsync(connection, database.Name, database.UserHost, CancellationToken.None);
        }
        catch (DatabaseHostException ex)
        {
            LogDatabaseLeft(database.Name, host.Name, ex.Message);
        }
    }

    private async Task ReleaseAsync(ServerDatabase database)
    {
        db.ServerDatabases.Remove(database);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<(ServerDatabase? Database, DatabaseHost? Host)> FindAsync(ServerId serverId, Guid databaseId, bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? db.ServerDatabases : db.ServerDatabases.AsNoTracking();
        var database = await query.SingleOrDefaultAsync(d => d.Id == new ServerDatabaseId(databaseId) && d.ServerId == serverId, cancellationToken);
        return database is null
            ? (null, null)
            : (database, await db.DatabaseHosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == database.HostId, cancellationToken));
    }

    private async Task<NodeId> NodeOfAsync(ServerId serverId, CancellationToken cancellationToken) =>
        await db.Servers.AsNoTracking().Where(s => s.Id == serverId).Select(s => s.NodeId).SingleAsync(cancellationToken);

    /// <summary>
    /// What a server's managers are told when the host fails: not what it said, which names the panel's account and
    /// addresses (settings managers see that when they test the host, and the log has it).
    /// </summary>
    private DomainError HostFailed(DatabaseHost host, DatabaseHostException exception)
    {
        LogHostFailed(host.Name, exception.Message);
        return DatabaseErrors.HostUnreachable;
    }

    private static ServerDatabaseView View(ServerDatabase database, DatabaseHost host)
    {
        var (address, port) = host.AddressForServers;
        return new ServerDatabaseView(database.Id.Value, database.Name, database.Name, host.Id.Value, host.Name, address, port, database.UserHost,
            database.CreatedAt, database.PasswordChangedAt);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database host {Host} failed a server's request: {Reason}")]
    private partial void LogHostFailed(string host, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database {Database} was left on the host {Host} when its server was deleted: {Reason}")]
    private partial void LogDatabaseLeft(string database, string host, string reason);
}
