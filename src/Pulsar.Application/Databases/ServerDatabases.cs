using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Databases;

/// <param name="Engine">What its host runs.</param>
/// <param name="Host">The host's name in the panel.</param>
/// <param name="Address">Where the game server connects (the host's address for servers).</param>
/// <param name="UserHost">Where its user may connect from (MySQL; % on PostgreSQL, whose own settings say).</param>
/// <param name="ForGame">The server's game keeps its data in it (1.0.37): it cannot be deleted while it does.</param>
public sealed record ServerDatabaseView(
    Guid Id,
    DatabaseEngine Engine,
    string Name,
    string UserName,
    Guid HostId,
    string Host,
    string Address,
    int Port,
    string UserHost,
    DateTimeOffset CreatedAt,
    DateTimeOffset PasswordChangedAt,
    bool ForGame = false);

/// <param name="Hosts">The hosts the server's node may use (for creating one).</param>
/// <param name="Game">Where its game keeps its data; null when the game cannot be given a database.</param>
public sealed record ServerDatabaseList(int Limit, IReadOnlyList<ServerDatabaseView> Databases, IReadOnlyList<DatabaseHostChoice> Hosts,
    GameDatabaseChoice? Game = null);

/// <summary>What a choice of a server's game database did (1.0.37).</summary>
/// <param name="Previous">The database the game used before; null: its own files.</param>
public sealed record GameDatabaseChange(bool Changed, ServerDatabaseId? Previous);

/// <summary>Where a server's game keeps its data (Pulsar 1.0.37): its own files, or one of the server's databases.</summary>
/// <param name="Engine">What the game's manifest takes.</param>
/// <param name="DatabaseId">The database it uses (not counted in the server's limit); null: its own files.</param>
/// <param name="Supported">The node's agent is connected and gives games a database: one can be chosen.</param>
public sealed record GameDatabaseChoice(DatabaseEngine Engine, Guid? DatabaseId, bool Supported);

public sealed record DatabaseHostChoice(Guid Id, string Name, DatabaseEngine Engine);

/// <summary>A database with its password: when it was created, its password rotated, or asked for.</summary>
public sealed record ServerDatabaseCredentials(ServerDatabaseView Database, string Password);

/// <summary>
/// A server's MySQL/MariaDB (Phase 13) and PostgreSQL (1.0.37) databases: <c>server.databases.view</c> lists them, <c>server.databases.manage</c>
/// creates, deletes and rotates them and reads their passwords. Each one is created on its host first and kept only
/// when that worked; the server's limit is checked with its row locked, so two requests cannot both take the last one.
/// A game whose manifest takes a database (1.0.37) may be given one: <see cref="SetGameDatabaseAsync"/>.
/// </summary>
public sealed partial class ServerDatabases(
    IPulsarDbContext db,
    DatabaseHosts hosts,
    IDatabaseSecretProtector secrets,
    IDatabaseHostClient client,
    IRowLocks locks,
    IAuditLog audit,
    IAgentConnections connections,
    AgentSessionStore sessions,
    GameDatabaseTurns turns,
    TimeProvider clock,
    ILogger<ServerDatabases> logger)
{
    /// <summary>What follows the server's prefix in its game's database: <c>s{short id}_game</c>.</summary>
    public const string GameSuffix = "game";

    public async Task<Result<ServerDatabaseList>> ListAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serverId, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var all = await db.DatabaseHosts.AsNoTracking().OrderBy(h => h.Name).ToListAsync(cancellationToken);
        var databases = await db.ServerDatabases.AsNoTracking().Where(d => d.ServerId == serverId).OrderBy(d => d.Name).ToListAsync(cancellationToken);
        var game = await GameEngineAsync(server, cancellationToken) is { } engine
            ? new GameDatabaseChoice(engine, server.GameDatabaseId?.Value, AgentGivesDatabases(server.NodeId))
            : null;
        return new ServerDatabaseList(server.DatabaseLimit, [.. databases.Select(d => View(d, all.Single(h => h.Id == d.HostId), d.Id == server.GameDatabaseId))],
            [.. all.Where(h => h.Serves(server.NodeId)).Select(h => new DatabaseHostChoice(h.Id.Value, h.Name, h.Engine))], game);
    }

    /// <summary>
    /// Where the server's game keeps its data from its next start (the caller then sends the server's spec): a database
    /// on <paramref name="hostId"/>, or its own files (null). The game's database is <c>s{short id}_game</c>: made on the
    /// host the first time (not counted in the server's limit), and used again when the game goes back to it, with the
    /// data it had; going back to the game's own files keeps it, as one of the server's databases (counted again).
    /// </summary>
    /// <param name="applied">
    /// Sends the server's spec to its node once the choice changed anything: null when the node took it, otherwise why
    /// not, and the choice is undone. Within the server's turn, so another choice sees the outcome.
    /// </param>
    /// <returns>Whether anything changed.</returns>
    public async Task<Result<bool>> SetGameDatabaseAsync(ServerId serverId, Guid? hostId, Func<Task<DomainError?>> applied,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applied);
        using var turn = await turns.Turns.AcquireAsync(serverId, cancellationToken);
        var set = await ChooseGameDatabaseAsync(serverId, hostId, cancellationToken);
        if (!set.IsSuccess || !set.Value.Changed)
        {
            return set.IsSuccess ? false : set.Error!;
        }

        if (await applied() is { } refused)
        {
            await RevertGameDatabaseAsync(serverId, set.Value.Previous);
            return refused;
        }

        return true;
    }

    private async Task<Result<GameDatabaseChange>> ChooseGameDatabaseAsync(ServerId serverId, Guid? hostId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serverId, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        // A server on its way to another node gets the database it had there: the move checked that node can use it.
        if (await TransferGate.RunningAsync(db, server.Id, cancellationToken))
        {
            return ServerErrors.Transferring;
        }

        var unchanged = new GameDatabaseChange(false, server.GameDatabaseId);
        var changed = new GameDatabaseChange(true, server.GameDatabaseId);

        // The node gets the change with the server's spec right away: one that is away would go on giving the game the
        // database (specs are not sent again when it connects).
        if (connections.Find(server.NodeId) is not { } live || !sessions.IsActive(live.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        if (hostId is null)
        {
            var unlinked = await LinkAsync(server.Id, null, null, cancellationToken);
            return unlinked.IsSuccess ? unlinked.Value ? changed : unchanged : unlinked.Error!;
        }

        if (server.ActiveOperationType == OperationType.ServerDelete)
        {
            return ServerErrors.Busy;
        }

        if (await GameEngineAsync(server, cancellationToken) is not { } engine)
        {
            return DatabaseErrors.GameUnsupported;
        }

        var host = await db.DatabaseHosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == new DatabaseHostId(hostId.Value), cancellationToken);
        if (host is null)
        {
            return DatabaseErrors.HostNotFound;
        }

        if (host.Engine != engine)
        {
            return DatabaseErrors.EngineMismatch(engine);
        }

        if (!host.Serves(server.NodeId))
        {
            return DatabaseErrors.HostNotAllowed;
        }

        // An agent that would not give the game its database would start it on its own files instead.
        if (!AgentGivesDatabases(server.NodeId))
        {
            return ServerErrors.GameDatabaseUnsupported;
        }

        var name = ServerDatabase.NameFor(server.ShortId, GameSuffix)!;
        var existing = await db.ServerDatabases.AsNoTracking().SingleOrDefaultAsync(d => d.ServerId == server.Id && d.Name == name, cancellationToken);
        if (server.GameDatabaseId is { } current)
        {
            return existing?.Id == current && existing.HostId == host.Id ? unchanged : DatabaseErrors.GameDatabaseSet;
        }

        if (existing is not null)
        {
            if (existing.HostId != host.Id)
            {
                return DatabaseErrors.GameNameTaken;
            }

            // The one it had before: its data is there.
            var linked = await LinkAsync(server.Id, existing, host, cancellationToken);
            return linked.IsSuccess ? linked.Value ? changed : unchanged : linked.Error!;
        }

        var created = await CreateOnHostAsync(server, host, GameSuffix, forGame: true, cancellationToken);
        return created.IsSuccess ? changed : created.Error!;
    }

    /// <summary>
    /// The server's game database (1.0.37) as its role reaches it from the panel, for a dump or a restore, and the host's
    /// account (a restore makes and swaps databases with it); null when the game has none.
    /// </summary>
    public async Task<Result<(GameDatabaseTarget Target, DatabaseHostConnection Admin)?>> GameTargetAsync(Server server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.GameDatabaseId is not { } id)
        {
            return ((GameDatabaseTarget, DatabaseHostConnection)?)null;
        }

        var database = await db.ServerDatabases.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        var host = database is null ? null : await db.DatabaseHosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == database.HostId, cancellationToken);
        if (database is null || host is null)
        {
            return ((GameDatabaseTarget, DatabaseHostConnection)?)null;
        }

        if (hosts.Connection(host) is not { } admin)
        {
            return DatabaseErrors.HostPasswordUnreadable;
        }

        try
        {
            return ((GameDatabaseTarget, DatabaseHostConnection)?)(
                new GameDatabaseTarget(host.Host, host.Port, database.Name, secrets.Unprotect(database.PasswordProtected), host.RequireTls), admin);
        }
        catch (CryptographicException)
        {
            return DatabaseErrors.PasswordUnreadable;
        }
    }

    /// <summary>Puts back the database the game used before a choice its node did not take (<see cref="SetGameDatabaseAsync"/>).</summary>
    private async Task RevertGameDatabaseAsync(ServerId serverId, ServerDatabaseId? previous)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await locks.LockServerAsync(serverId, CancellationToken.None);
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == serverId, CancellationToken.None);
        if (server is null || server.GameDatabaseId == previous
            || (previous is { } id && !await db.ServerDatabases.AnyAsync(d => d.Id == id && d.ServerId == serverId, CancellationToken.None)))
        {
            return;
        }

        server.SetGameDatabase(previous, clock.GetUtcNow());
        audit.Record(AuditActions.Server.GameDatabaseChanged, metadata: new { reverted = true, reason = "node_not_updated" })
            .WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
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

        return await CreateOnHostAsync(server, host, name, forGame: false, cancellationToken);
    }

    /// <summary>
    /// Makes the database on its host and keeps it; the game's (<paramref name="forGame"/>) is not counted in the
    /// server's limit, and is the game's once it is kept.
    /// </summary>
    private async Task<Result<ServerDatabaseCredentials>> CreateOnHostAsync(Server server, DatabaseHost host, string name, bool forGame,
        CancellationToken cancellationToken)
    {
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
        if (await ReserveAsync(server.Id, database, forGame, cancellationToken) is { } refused)
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
        if (forGame)
        {
            var tracked = await db.Servers.SingleAsync(s => s.Id == server.Id, CancellationToken.None);
            tracked.SetGameDatabase(database.Id, clock.GetUtcNow());
            audit.Record(AuditActions.Server.GameDatabaseChanged, metadata: new { database.Name, Host = host.Name })
                .WithServer(server.Id, server.NodeId);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return new ServerDatabaseCredentials(View(database, host, forGame), password);
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
        return new ServerDatabaseCredentials(View(database, host, await IsGamesAsync(serverId, database.Id, cancellationToken)), password);
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

        // The game's (1.0.37): its node must take the new password with the server's spec, or the game could not sign in.
        if (await db.Servers.AsNoTracking().Where(s => s.Id == serverId && s.GameDatabaseId == database.Id).Select(s => (NodeId?)s.NodeId)
                .SingleOrDefaultAsync(cancellationToken) is { } gameNode && !AgentGivesDatabases(gameNode))
        {
            return ServerErrors.GameDatabaseUnsupported;
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
        return new ServerDatabaseCredentials(View(database, host, await IsGamesAsync(serverId, database.Id, CancellationToken.None)), password);
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

        // Its game's data is there: the game goes back to its own files first (which takes the server's lock, as this does).
        await locks.LockServerAsync(serverId, cancellationToken);
        if (await db.Servers.AsNoTracking().AnyAsync(s => s.Id == serverId && s.GameDatabaseId == database.Id, cancellationToken))
        {
            return DatabaseErrors.InUseByGame;
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
        // The server's row and its databases' go together, and its row points at its game's (1.0.37): that goes first, in
        // the caller's transaction, or the two would wait for each other.
        if (server.GameDatabaseId is not null)
        {
            server.SetGameDatabase(null, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        var databases = await db.ServerDatabases.Where(d => d.ServerId == server.Id).ToListAsync(cancellationToken);
        // Why a host was left alone: an error code for the audit log, the host's own words (they name the panel's account
        // and the host's address) for the panel's log only.
        var failed = new Dictionary<DatabaseHostId, (string Code, string Detail)?>();
        foreach (var database in databases)
        {
            var host = await db.DatabaseHosts.AsNoTracking().SingleAsync(h => h.Id == database.HostId, cancellationToken);
            if (!failed.TryGetValue(host.Id, out var left))
            {
                if (hosts.Connection(host) is not { } connection)
                {
                    left = failed[host.Id] = (DatabaseErrors.HostPasswordUnreadable.Code, DatabaseErrors.HostPasswordUnreadable.Message);
                }
                else
                {
                    try
                    {
                        await client.DropAsync(connection, database.Name, database.UserHost, cancellationToken);
                    }
                    catch (DatabaseHostException ex)
                    {
                        left = failed[host.Id] = (DatabaseErrors.HostUnreachable.Code, ex.Message);
                    }
                }
            }

            var leftBecause = left?.Code;
            if (leftBecause is not null)
            {
                LogDatabaseLeft(database.Name, host.Name, left!.Value.Detail);
            }

            db.ServerDatabases.Remove(database);
            audit.Record(AuditActions.Server.DatabaseDeleted, metadata: new { database.Name, Host = host.Name, dropped = leftBecause is null, leftBecause })
                .WithServer(server.Id, server.NodeId);
        }
    }

    /// <summary>
    /// Adds the row with the server's row locked: its limit (which its game's database is not counted in) and the name are
    /// checked against what is there now; the game's (<paramref name="forGame"/>) only while the game has none.
    /// </summary>
    private async Task<DomainError?> ReserveAsync(ServerId serverId, ServerDatabase database, bool forGame, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockServerAsync(serverId, cancellationToken);
        var server = await db.Servers.AsNoTracking().Where(s => s.Id == serverId)
            .Select(s => new { s.DatabaseLimit, s.ActiveOperationType, s.GameDatabaseId })
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
        var existing = await db.ServerDatabases.Where(d => d.ServerId == serverId).Select(d => new { d.Id, d.Name }).ToListAsync(cancellationToken);
        if (existing.Any(d => d.Name == database.Name))
        {
            return DatabaseErrors.NameTaken;
        }

        if (forGame ? server.GameDatabaseId is not null : existing.Count(d => d.Id != server.GameDatabaseId) >= server.DatabaseLimit)
        {
            return forGame ? DatabaseErrors.GameDatabaseSet : DatabaseErrors.LimitReached;
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

    /// <summary>The game uses <paramref name="database"/> (on <paramref name="host"/>) from its next start; null: its own files.</summary>
    private async Task<Result<bool>> LinkAsync(ServerId serverId, ServerDatabase? database, DatabaseHost? host, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockServerAsync(serverId, cancellationToken);
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == serverId, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (server.GameDatabaseId == database?.Id)
        {
            return false;
        }

        // Another request gave it one meanwhile: the caller chose from what it saw before.
        if (database is not null && server.GameDatabaseId is not null)
        {
            return DatabaseErrors.GameDatabaseSet;
        }

        if (database is not null && !await db.ServerDatabases.AnyAsync(d => d.Id == database.Id, cancellationToken))
        {
            return DatabaseErrors.NotFound;
        }

        var before = server.GameDatabaseId is { } previous
            ? await db.ServerDatabases.AsNoTracking().Where(d => d.Id == previous).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken)
            : null;
        server.SetGameDatabase(database?.Id, clock.GetUtcNow());
        audit.Record(AuditActions.Server.GameDatabaseChanged, metadata: new { database?.Name, Host = host?.Name, before })
            .WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    /// <summary>The engine of the database the server's game takes; null when its manifest takes none.</summary>
    private async Task<DatabaseEngine?> GameEngineAsync(Server server, CancellationToken cancellationToken)
    {
        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        GameManifest manifest;
        try
        {
            manifest = ServerSpecs.Parse(template);
        }
        catch (DomainErrorException)
        {
            return null;
        }

        return manifest.Database?.Engine switch
        {
            "postgres" => DatabaseEngine.Postgres,
            "mysql" => DatabaseEngine.Mysql,
            _ => null,
        };
    }

    /// <summary>The node's agent is connected and gives games the database the panel sends.</summary>
    private bool AgentGivesDatabases(NodeId node) =>
        connections.Find(node) is { } live && sessions.IsActive(live.SessionId)
        && live.Capabilities.Contains(AgentCapabilities.GameDatabase, StringComparer.Ordinal);

    private Task<bool> IsGamesAsync(ServerId serverId, ServerDatabaseId database, CancellationToken cancellationToken) =>
        db.Servers.AsNoTracking().AnyAsync(s => s.Id == serverId && s.GameDatabaseId == database, cancellationToken);

    private static ServerDatabaseView View(ServerDatabase database, DatabaseHost host, bool forGame)
    {
        var (address, port) = host.AddressForServers;
        return new ServerDatabaseView(database.Id.Value, host.Engine, database.Name, database.Name, host.Id.Value, host.Name, address, port, database.UserHost,
            database.CreatedAt, database.PasswordChangedAt, forGame);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database host {Host} failed a server's request: {Reason}")]
    private partial void LogHostFailed(string host, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database {Database} was left on the host {Host} when its server was deleted: {Reason}")]
    private partial void LogDatabaseLeft(string database, string host, string reason);
}
