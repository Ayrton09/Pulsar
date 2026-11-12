using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Databases;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <param name="Sha256">Lowercase hex, once the backup is complete.</param>
/// <param name="DurationSeconds">How long the node took to make it.</param>
/// <param name="OperationId">The operation making it (to follow its progress or cancel it).</param>
/// <param name="RemoteStatus">Its copy in remote storage.</param>
/// <param name="RemoteError">Why the copy failed.</param>
/// <param name="CopyMode">How the node read the files (null: made before Phase 15).</param>
/// <param name="SnapshotError">Why a running game's files were read live rather than from a snapshot (a <c>snapshot.*</c> code).</param>
/// <param name="GameSave">How the game answered when asked to save first (null: not asked).</param>
/// <param name="ErrorCode">
/// Why a failed backup failed, as its operation's code (the browser shows its translation, <see cref="ErrorMessage"/>
/// without one); null when the panel ended it itself.
/// </param>
/// <param name="HasGameDatabase">It holds the dump of the game's database (1.0.37), which a full restore puts back.</param>
public sealed record BackupView(
    Guid Id,
    string Name,
    BackupStatus Status,
    long? Size,
    int? FileCount,
    int? SkippedFiles,
    string? Sha256,
    bool Locked,
    OperationRequester CreatedVia,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    double? DurationSeconds,
    string? ErrorMessage,
    Guid? OperationId,
    RemoteCopyStatus RemoteStatus,
    string? RemoteError,
    BackupCopyMode? CopyMode,
    string? SnapshotError,
    GameSaveResult? GameSave,
    string? ErrorCode = null,
    bool HasGameDatabase = false);

/// <param name="Limit">How many backups retention keeps (locked ones besides); null keeps them all.</param>
/// <summary>A storage a server can copy its backups to (its name only: the rest is the panel's settings).</summary>
public sealed record BackupStorageOption(Guid Id, string Name);

/// <param name="StorageId">Where new backups are copied besides the node; null: on the node only.</param>
/// <param name="Storages">The storages to choose from.</param>
public sealed record BackupList(IReadOnlyList<BackupView> Backups, int? Limit, Guid? StorageId, IReadOnlyList<BackupStorageOption> Storages);

/// <summary>
/// A server's backups (ADR-0012; docs/architecture/05 and 08): made and restored by its node as operations, deleted
/// and downloaded through plain commands. The archives stay on the node, and with a <see cref="BackupStorage"/> the node
/// also copies each one there (and brings it back when it no longer has it); the panel keeps their metadata, applies
/// the retention limit after every new backup, and audits every request.
/// </summary>
public sealed partial class ServerBackups(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    ServerOperations operations,
    FileTransfers transfers,
    BackupStorages storages,
    ServerDatabases gameDatabases,
    IGameDatabaseDumps dumps,
    GameDatabaseRestores waitingRestores,
    IRowLocks locks,
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock,
    ILogger<ServerBackups> logger)
{
    /// <summary>How many of a restore's picked paths its audit entry names (it counts them all).</summary>
    private const int MaxAuditedPaths = 20;

    public async Task<Result<BackupList>> ListAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return server is null ? ServerErrors.NotFound : await ListOfAsync(server, cancellationToken);
    }

    /// <summary>
    /// Starts a backup on the server's node; it may keep running meanwhile (the node asks the game to save and reads a
    /// snapshot of its volume when it can; otherwise files it writes then may be captured half written).
    /// <paramref name="name"/> defaults to the time it was asked for.
    /// </summary>
    /// <param name="recorded">Called with the operation's id just before it is first saved: what follows it is saved with it.</param>
    /// <param name="whole">
    /// Every file of the server, whatever its game's backup rules (a clone's or a transfer's copy, 1.0.17); kept on its
    /// node only, never copied to remote storage. Never the game's database (1.0.37), which every other backup holds.
    /// </param>
    public async Task<Result<BackupView>> CreateAsync(ServerId id, string? name, OperationRequester via, CancellationToken cancellationToken,
        Action<OperationId>? recorded = null, bool whole = false)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        // A transfer's own backups are the panel's; others wait until it ends (1.0.16).
        if (via != OperationRequester.System && await TransferGate.RunningAsync(db, id, cancellationToken))
        {
            return ServerErrors.Transferring;
        }

        if (Connection(server.NodeId, AgentCapabilities.CreateBackup) is { IsSuccess: false } connection)
        {
            return connection.Error!;
        }

        if (whole && Connection(server.NodeId, AgentCapabilities.WholeBackup) is { IsSuccess: false })
        {
            return ServerErrors.CopyUnsupported;
        }

        if (connections.Find(server.NodeId)?.BelowFloor(DiskUses.Backups) is not null)
        {
            return NodeErrors.DiskLow;
        }

        var now = clock.GetUtcNow();
        var requestedBy = via == OperationRequester.User ? request.UserId : null;
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, requestedBy, via, now);
        if (server.CheckOperation(operation) is { IsSuccess: false } unavailable)
        {
            return unavailable.Error!;
        }

        var backupName = string.IsNullOrWhiteSpace(name) ? DefaultName(now) : name;
        if (Backup.CheckName(backupName) is { IsSuccess: false } invalidName)
        {
            return invalidName.Error!;
        }

        // The game's database (1.0.37), dumped first: a backup that should hold it and cannot is not made.
        PreparedDatabaseDump? preparation = null;
        if (!whole && server.GameDatabaseId is not null)
        {
            var dumped = await GameDatabaseDumpAsync(server, cancellationToken);
            if (!dumped.IsSuccess)
            {
                return dumped.Error!;
            }

            preparation = dumped.Value;
        }

        // RunAsync checks admission again when saving, since another operation may have won while the dump was made.
        // Until admission is saved, a refusal or exception releases the dump's ticket and file at once. After that a
        // send (or saving its accepted outcome) may fail while the node is using it: keep its ordinary transfer lifetime,
        // including a failed operation whose acknowledgement was lost.
        var handedToNode = false;
        try
        {
            var database = preparation?.Dump;
            var created = Backup.Create(server, backupName, operation, requestedBy, via, now, database?.SizeBytes);
            if (!created.IsSuccess)
            {
                return created.Error!;
            }

            var backup = created.Value;

            var remote = whole ? null : await RemoteForNewAsync(server, backup, cancellationToken);
            var run = await operations.RunAsync(server, operation, new CreateBackupCommand(server.Id.Value, backup.Id.Value, remote, whole, database), () =>
            {
                db.Backups.Add(backup);
                recorded?.Invoke(operation.Id);
                audit.Record(AuditActions.Server.BackupRequested, metadata: new { backup = backup.Id.Value, backup.Name })
                    .WithServer(server.Id, server.NodeId);
            }, cancellationToken, admitted: () => handedToNode = true);
            if (!run.IsSuccess)
            {
                return run.Error!;
            }

            if (run.Value.Status == OperationStatus.Failed)
            {
                // Acceptance was refused or unconfirmed. Record the failure, but a lost acknowledgement does not
                // prove that the node stopped using the dump's ticket.
                backup.Ended(BackupStatus.Failed, run.Value.ErrorMessage ?? run.Value.ErrorCode, clock.GetUtcNow());
                await db.SaveChangesAsync(CancellationToken.None);
            }

            await db.Entry(backup).ReloadAsync(cancellationToken);
            return View(backup);
        }
        finally
        {
            if (!handedToNode && preparation is { } unused)
            {
                unused.Upload.Abort();
                await unused.Released;
            }
        }
    }

    /// <summary>
    /// Puts the backup's files back (the server must be stopped); the server cannot start until it ends. A
    /// <paramref name="clean"/> restore first deletes the files the backup's rules select that it does not have.
    /// </summary>
    /// <param name="paths">
    /// Only these files and folders of the backup (Pulsar 1.0.18; a clean restore then deletes newer files only under
    /// them); null: all of it.
    /// </param>
    public async Task<Result> RestoreAsync(ServerId id, BackupId backupId, bool clean, CancellationToken cancellationToken,
        IReadOnlyList<string>? paths = null)
    {
        if (paths is not null)
        {
            var picked = Picked(paths);
            if (!picked.IsSuccess)
            {
                return picked.Error!;
            }

            paths = picked.Value;
        }

        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var backup = server is null ? null : await db.Backups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == backupId && b.ServerId == id, cancellationToken);
        if (server is null || backup is null)
        {
            return server is null ? ServerErrors.NotFound : ServerErrors.BackupNotFound;
        }

        if (backup is not { Status: BackupStatus.Completed, Sha256: { } sha256 })
        {
            return ServerErrors.BackupNotCompleted;
        }

        if (clean && backup.CheckCleanRestore() is { IsSuccess: false } incomplete)
        {
            return incomplete;
        }

        if (Connection(server.NodeId, AgentCapabilities.RestoreBackup) is { IsSuccess: false } connection)
        {
            return connection.Error!;
        }

        // An older agent would ignore the request to clean and only write the backup's files over the others.
        if (clean && Connection(server.NodeId, AgentCapabilities.CleanRestore) is { IsSuccess: false })
        {
            return ServerErrors.CleanRestoreUnsupported;
        }

        // And one asked for some of its files would restore all of them.
        if (paths is not null && Connection(server.NodeId, AgentCapabilities.PartialRestore) is { IsSuccess: false })
        {
            return ServerErrors.PartialRestoreUnsupported;
        }

        // The game's database comes back first with a full restore (1.0.37), through the panel; a partial one leaves it.
        var database = await GameDatabaseRestoreAsync(server, backup, paths, cancellationToken);
        if (!database.IsSuccess)
        {
            return database.Error!;
        }

        var receiving = database.Value;
        var operation = Operation.Create(OperationType.BackupRestore, server.NodeId, server.Id, request.UserId, OperationRequester.User, clock.GetUtcNow());
        if (receiving is { } waiting)
        {
            // Waited for until it comes, or its restore ends without it (GameDatabaseRestores.Ended), however soon that is.
            waitingRestores.Add(operation.Id, waiting.Download);
            _ = RestoreGameDatabaseAsync(server.Id, operation.Id, waiting.Download, backup.GameDatabaseBytes!.Value, waiting.Target, waiting.Admin);
        }

        var remote = await RemoteOfAsync(backup, cancellationToken);
        var run = await operations.RunAsync(server, operation,
            new RestoreBackupCommand(server.Id.Value, backup.Id.Value, Convert.ToHexStringLower(sha256), remote, clean, backup.SizeBytes, Paths: paths,
                DatabaseTicket: receiving?.Download.Ticket),
            () => audit.Record(AuditActions.Server.BackupRestoreRequested, metadata: paths is null
                    ? new { backup = backup.Id.Value, backup.Name, clean, gameDatabase = receiving is not null }
                    : new { backup = backup.Id.Value, backup.Name, clean, paths = paths.Take(MaxAuditedPaths).ToList(), pathCount = paths.Count })
                .WithServer(server.Id, server.NodeId),
            cancellationToken);
        if (receiving is not null && (!run.IsSuccess || run.Value.Status is not (OperationStatus.Queued or OperationStatus.Running)))
        {
            waitingRestores.Ended(operation.Id);
        }

        return run.IsSuccess ? Result.Success() : run.Error!;
    }

    /// <summary>
    /// One folder of the backup (<see cref="ServerFilePath.Root"/>: its top), as its node reads it from the archive's index
    /// (Pulsar 1.0.18): what a restore of some of it can pick. The archive must be on the node. Whoever may download the
    /// backup may see what it holds.
    /// </summary>
    public async Task<Result<FileListing>> ListFilesAsync(ServerId id, BackupId backupId, string? path, CancellationToken cancellationToken)
    {
        path ??= ServerFilePath.Root;
        if (ServerFilePath.Error(path, allowRoot: true) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var backup = server is null ? null : await db.Backups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == backupId && b.ServerId == id, cancellationToken);
        if (server is null || backup is null)
        {
            return server is null ? ServerErrors.NotFound : ServerErrors.BackupNotFound;
        }

        if (backup.Status != BackupStatus.Completed)
        {
            return ServerErrors.BackupNotCompleted;
        }

        if (Connection(server.NodeId, AgentCapabilities.RestoreBackup) is { IsSuccess: false } connection)
        {
            return connection.Error!;
        }

        if (Connection(server.NodeId, AgentCapabilities.PartialRestore) is { IsSuccess: false }
            || Connection(server.NodeId, AgentCapabilities.ListBackupFiles) is { IsSuccess: false })
        {
            return ServerErrors.PartialRestoreUnsupported;
        }

        var ack = Ack(await connections.SendAsync(server.NodeId, new ListBackupFilesCommand(server.Id.Value, backup.Id.Value, path), request.UserId,
            cancellationToken));
        if (!ack.IsSuccess)
        {
            return ack.Error!;
        }

        return ack.Value.Result is FileListResult listing
            ? new FileListing(path, listing.Entries, listing.Truncated, listing.Skipped)
            : ServerErrors.CommandRejected;
    }

    /// <summary>
    /// The files and folders a restore of some of a backup picks: 1 to <see cref="RestoreBackupCommand.MaxPaths"/> plain
    /// paths below the server's root, never in Pulsar's own folder; one inside another picked is dropped (the folder has it).
    /// </summary>
    private static Result<IReadOnlyList<string>> Picked(IReadOnlyList<string> paths)
    {
        if (paths.Count is 0 or > RestoreBackupCommand.MaxPaths)
        {
            return ServerErrors.FilePathInvalid($"Pick from 1 to {RestoreBackupCommand.MaxPaths} files and folders to restore.");
        }

        foreach (var path in paths)
        {
            if (ServerFilePath.Error(path) is { } error)
            {
                return ServerErrors.FilePathInvalid(error);
            }

            if (path.Split('/')[0].Equals(".pulsar", StringComparison.OrdinalIgnoreCase))
            {
                return ServerErrors.FilePathInvalid("Pulsar's own files are never restored on their own.");
            }
        }

        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Where(p => !distinct.Any(other => ServerFilePath.IsInside(p, other))).ToList();
    }

    /// <summary>Deletes the backup from its node, then forgets it. Locked backups and backups being made are kept.</summary>
    public async Task<Result> DeleteAsync(ServerId id, BackupId backupId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var backup = server is null ? null : await db.Backups.SingleOrDefaultAsync(b => b.Id == backupId && b.ServerId == id, cancellationToken);
        if (server is null || backup is null)
        {
            return server is null ? ServerErrors.NotFound : ServerErrors.BackupNotFound;
        }

        if (backup.IsLocked)
        {
            return ServerErrors.BackupLocked;
        }

        if (backup.Status == BackupStatus.Creating)
        {
            return ServerErrors.BackupNotCompleted;
        }

        var deleted = await RemoveAsync(server, backup, request.Actor, reason: null, cancellationToken);
        return deleted;
    }

    /// <summary>A backup being deleted (its archive going) is waited for, and then not found.</summary>
    public async Task<Result<BackupView>> SetLockedAsync(ServerId id, BackupId backupId, bool locked, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var backup = await db.Backups.SingleOrDefaultAsync(b => b.Id == backupId && b.ServerId == id, cancellationToken);
            if (backup is null)
            {
                return await db.Servers.AnyAsync(s => s.Id == id, cancellationToken) ? ServerErrors.BackupNotFound : ServerErrors.NotFound;
            }

            var changed = backup.SetLocked(locked);
            if (!changed.IsSuccess)
            {
                return changed.Error!;
            }

            audit.Record(locked ? AuditActions.Server.BackupLocked : AuditActions.Server.BackupUnlocked, metadata: new { backup = backup.Id.Value, backup.Name })
                .WithServer(backup.ServerId, backup.NodeId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return View(backup);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // Deleted (or changed by its node) meanwhile: read it again.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Sets how many backups to keep; backups beyond a lower limit are deleted now.</summary>
    public async Task<Result<BackupList>> SetLimitAsync(ServerId id, int? limit, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var changed = server.SetBackupLimit(limit, clock.GetUtcNow());
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(AuditActions.Server.BackupLimitChanged, metadata: new { limit }).WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent reported a state change in between: apply the limit again on the fresh row.
            await db.Entry(server).ReloadAsync(cancellationToken);
            server.SetBackupLimit(limit, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        await PruneAsync(id, cancellationToken);
        return await ListOfAsync(server, cancellationToken);
    }

    /// <summary>Backups that did not complete kept per server: they hold no archive, and the newest say what went wrong.</summary>
    public const int KeptUnfinished = 20;

    /// <summary>A server's newest operations, kept whatever their age (its page lists the last ones).</summary>
    public const int KeptOperations = 50;

    /// <summary>How long a server's finished operations are kept beyond <see cref="KeptOperations"/>.</summary>
    public static readonly TimeSpan OperationsKeptFor = TimeSpan.FromDays(30);

    /// <summary>
    /// Retention: deletes the oldest complete, unlocked backups beyond the server's limit, the rows of backups that did
    /// not complete beyond <see cref="KeptUnfinished"/>, and the server's old finished operations (1.0.36: a schedule that
    /// kept failing left a row of each every time, forever). Runs after every new backup and when the limit changes;
    /// whatever it cannot delete now (the node is disconnected) goes at the next run.
    /// </summary>
    public async Task PruneAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return;
        }

        // A clone or a transfer copies the server's newest whole backup, which may be the oldest one kept: retention waits
        // until it ends, and runs again after the next backup.
        if (await CopyingAsync(id, cancellationToken))
        {
            return;
        }

        await PruneUnfinishedAsync(server, cancellationToken);
        await PruneOperationsAsync(server, cancellationToken);
        if (server.BackupLimit is not { } limit)
        {
            return;
        }

        var extra = await db.Backups
            .Where(b => b.ServerId == id && b.Status == BackupStatus.Completed && !b.IsLocked)
            .OrderByDescending(b => b.CreatedAt)
            .Skip(limit)
            .ToListAsync(cancellationToken);
        foreach (var backup in extra)
        {
            var removed = await RemoveAsync(server, backup, AuditActor.System, reason: "retention", cancellationToken);
            if (!removed.IsSuccess && removed.Error != ServerErrors.BackupLocked)
            {
                LogPruneStopped(server.Id, backup.Id);
                return;
            }
        }
    }

    private async Task<bool> CopyingAsync(ServerId id, CancellationToken cancellationToken)
    {
        if (await TransferGate.RunningAsync(db, id, cancellationToken))
        {
            return true;
        }

        // A clone's sequence names its source in its first step (ServerManagement.CloneAsync).
        var source = id.Value.ToString();
        return (await db.ServerSequences.AsNoTracking()
                .Where(q => q.Origin == Domain.Scheduling.SequenceOrigin.Clone && q.Status == Domain.Scheduling.SequenceStatus.Running)
                .ToListAsync(cancellationToken))
            .Any(q => q.Steps is [{ Text: { } text }, ..] && text == source);
    }

    // No archive to delete on the node: the rows go at once, with one audit entry for them all.
    private async Task PruneUnfinishedAsync(Server server, CancellationToken cancellationToken)
    {
        var oldestKept = await db.Backups.AsNoTracking()
            .Where(b => b.ServerId == server.Id && (b.Status == BackupStatus.Failed || b.Status == BackupStatus.Cancelled))
            .OrderByDescending(b => b.CreatedAt)
            .Skip(KeptUnfinished - 1)
            .Select(b => (DateTimeOffset?)b.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldestKept is not { } cutoff)
        {
            return;
        }

        var count = await db.Backups
            .Where(b => b.ServerId == server.Id && (b.Status == BackupStatus.Failed || b.Status == BackupStatus.Cancelled) && !b.IsLocked
                && b.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        if (count > 0)
        {
            audit.Record(AuditActions.Server.BackupDeleted, AuditOutcome.Success, AuditActor.System, new { count, reason = "retention_unfinished" })
                .WithServer(server.Id, server.NodeId);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // Finished, older than OperationsKeptFor and beyond the newest KeptOperations: what refers to them (a backup, a mod,
    // a scheduled run) keeps its row, without the link.
    private async Task PruneOperationsAsync(Server server, CancellationToken cancellationToken)
    {
        var oldestKept = await db.Operations.AsNoTracking()
            .Where(o => o.ServerId == server.Id)
            .OrderByDescending(o => o.CreatedAt)
            .Skip(KeptOperations - 1)
            .Select(o => (DateTimeOffset?)o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldestKept is not { } kept)
        {
            return;
        }

        var cutoff = clock.GetUtcNow() - OperationsKeptFor;
        var before = kept < cutoff ? kept : cutoff;
        await db.Operations
            .Where(o => o.ServerId == server.Id && o.CreatedAt < before
                && (o.Status == OperationStatus.Succeeded || o.Status == OperationStatus.Failed || o.Status == OperationStatus.Cancelled
                    || o.Status == OperationStatus.Interrupted))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Starts the backup's way from its node to the browser (streamed, never stored on the panel).</summary>
    public async Task<Result<FileContent>> OpenAsync(ServerId id, BackupId backupId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var backup = server is null ? null : await db.Backups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == backupId && b.ServerId == id, cancellationToken);
        if (server is null || backup is null)
        {
            return server is null ? ServerErrors.NotFound : ServerErrors.BackupNotFound;
        }

        if (backup.Status != BackupStatus.Completed)
        {
            return ServerErrors.BackupNotCompleted;
        }

        if (Connection(server.NodeId, AgentCapabilities.SendBackup) is { IsSuccess: false } connection)
        {
            return connection.Error!;
        }

        if (transfers.BeginDownload(server.NodeId, request.UserId, request.ApiKeyId) is not { } download)
        {
            return ServerErrors.FileTransfersBusy;
        }

        try
        {
            var remote = await RemoteOfAsync(backup, cancellationToken);
            var ack = Ack(await connections.SendAsync(server.NodeId, new SendBackupCommand(server.Id.Value, backup.Id.Value, download.Ticket, remote),
                request.UserId, cancellationToken));
            if (!ack.IsSuccess || ack.Value.Result is not FileEntryResult file)
            {
                download.Dispose();
                return ack.IsSuccess ? ServerErrors.CommandRejected : ack.Error!;
            }

            await download.WaitForClaimAsync(cancellationToken);
            var (content, length) = await download.WaitForContentAsync(cancellationToken);
            audit.Record(AuditActions.Server.BackupDownloaded, AuditOutcome.Success, new { backup = backup.Id.Value, backup.Name, size = length })
                .WithServer(server.Id, server.NodeId);
            await db.SaveChangesAsync(cancellationToken);
            var entry = file.Entry with { Name = DownloadName(server, backup) };
            return new FileContent(new FileDetails(entry, file.Version), content, length, download);
        }
        catch (Exception ex) when (ex is TimeoutException
            || (ex is OperationCanceledException && download.Aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
        {
            download.Dispose();
            return ServerErrors.FileTransferFailed;
        }
        catch
        {
            download.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes the archive on the node (complete backups only have one), then the row. The row is claimed first (locked
    /// until it is gone, and read again): a user locking it meanwhile waits and then finds it gone, and one who locked it
    /// just before keeps it.
    /// </summary>
    private async Task<Result> RemoveAsync(Server server, Backup backup, AuditActor actor, string? reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockBackupAsync(backup.Id, cancellationToken);
        await db.Entry(backup).ReloadAsync(cancellationToken);
        if (db.Entry(backup).State == EntityState.Detached)
        {
            return Result.Success(); // deleted by someone else meanwhile
        }

        if (backup.IsLocked)
        {
            return ServerErrors.BackupLocked;
        }

        if (backup.Status == BackupStatus.Creating)
        {
            return ServerErrors.BackupNotCompleted;
        }

        if (backup.Status == BackupStatus.Completed)
        {
            if (Connection(server.NodeId, AgentCapabilities.DeleteBackup) is { IsSuccess: false } connection)
            {
                return connection.Error!;
            }

            var remote = await RemoteOfAsync(backup, cancellationToken);
            // Once sent, its answer is waited for (the request given up included): the row must follow the archive.
            var ack = Ack(await connections.SendAsync(server.NodeId, new DeleteBackupCommand(server.Id.Value, backup.Id.Value, remote),
                actor.UserId, CancellationToken.None));
            if (!ack.IsSuccess)
            {
                return ack.Error!;
            }
        }

        // The archive is gone: the row goes with it, audited (nobody else can change it until then).
        db.Backups.Remove(backup);
        audit.Record(AuditActions.Server.BackupDeleted, AuditOutcome.Success, actor, new { backup = backup.Id.Value, backup.Name, reason })
            .WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return Result.Success();
    }

    /// <summary>Where new backups are copied besides the node (null: nowhere). Backups already made keep their copies.</summary>
    public async Task<Result<BackupList>> SetStorageAsync(ServerId id, Guid? storageId, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var storage = storageId is { } wanted
            ? await db.BackupStorages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == new BackupStorageId(wanted), cancellationToken)
            : null;
        if (storageId is not null && storage is null)
        {
            return BackupStorageErrors.NotFound;
        }

        for (var attempt = 0; ; attempt++)
        {
            server.SetBackupStorage(storage?.Id, clock.GetUtcNow());
            audit.Record(AuditActions.Server.BackupStorageChanged, metadata: new { storage = storage?.Id.Value, storageName = storage?.Name })
                .WithServer(server.Id, server.NodeId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // The agent reported a state change in between: set it again on the fresh row.
                db.ChangeTracker.Clear();
                server = await db.Servers.SingleAsync(s => s.Id == id, cancellationToken);
            }
        }

        return await ListOfAsync(server, cancellationToken);
    }

    /// <summary>The copy target for a new backup, with the copy recorded on it; null without a storage (or with an agent that cannot copy).</summary>
    private async Task<RemoteBackupTarget?> RemoteForNewAsync(Server server, Backup backup, CancellationToken cancellationToken)
    {
        if (server.BackupStorageId is not { } storageId
            || await db.BackupStorages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == storageId, cancellationToken) is not { } storage)
        {
            return null;
        }

        if (connections.Find(server.NodeId) is { } agent && !agent.Capabilities.Contains(AgentCapabilities.RemoteBackups))
        {
            backup.CopyNotPossible("The node's agent is too old to copy backups to remote storage: update it.");
            return null;
        }

        backup.CopyTo(storage, server.ShortId);
        var target = storages.Target(storage, backup.RemoteKey!);
        if (target is null)
        {
            backup.CopyNotPossible("The storage's secret key cannot be read with this panel's keys: set it again.");
        }

        return target;
    }

    /// <summary>The target of a backup's copy, when it has one.</summary>
    private Task<RemoteBackupTarget?> RemoteOfAsync(Backup backup, CancellationToken cancellationToken) =>
        backup.RemoteStatus == RemoteCopyStatus.Copied ? storages.TargetAsync(backup.StorageId, backup.RemoteKey, cancellationToken) : Task.FromResult<RemoteBackupTarget?>(null);

    private async Task<BackupList> ListOfAsync(Server server, CancellationToken cancellationToken)
    {
        var backups = await db.Backups.AsNoTracking().Where(b => b.ServerId == server.Id).OrderByDescending(b => b.CreatedAt).ToListAsync(cancellationToken);
        var options = await db.BackupStorages.AsNoTracking().OrderBy(s => s.Name).Select(s => new BackupStorageOption(s.Id.Value, s.Name))
            .ToListAsync(cancellationToken);
        // A failed backup keeps its operation's message; the code, for the browser to translate, is the operation's.
        var failed = backups.Where(b => b.Status == BackupStatus.Failed && b.OperationId is not null).Select(b => b.OperationId!.Value).ToList();
        var codes = failed.Count == 0
            ? []
            : await db.Operations.AsNoTracking().Where(o => failed.Contains(o.Id) && o.ErrorCode != null)
                .ToDictionaryAsync(o => o.Id, o => o.ErrorCode, cancellationToken);
        return new BackupList(
            [.. backups.Select(b => View(b) with { ErrorCode = b.OperationId is { } op ? codes.GetValueOrDefault(op) : null })],
            server.BackupLimit, server.BackupStorageId?.Value, options);
    }

    private Result Connection(NodeId node, string capability)
    {
        if (connections.Find(node) is not { } live || !sessions.IsActive(live.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        return live.Capabilities.Contains(capability, StringComparer.Ordinal) ? Result.Success() : ServerErrors.BackupsUnsupported;
    }

    private static Result<CommandAck> Ack(CommandAck? ack) => ack switch
    {
        null => NodeErrors.NotConnected,
        { Status: AckStatus.Accepted or AckStatus.AlreadyInState } => ack,
        { Status: AckStatus.Busy } => ServerErrors.Busy,
        { Status: AckStatus.Rejected, ErrorCode: { } code } when code.StartsWith("backups.", StringComparison.Ordinal) =>
            ServerErrors.BackupRejected(code, ack.Message ?? code),
        { Status: AckStatus.Rejected, ErrorCode: { } code } when code.StartsWith("files.", StringComparison.Ordinal) =>
            ServerErrors.FileRejected(code, ack.Message ?? code),
        _ => ServerErrors.CommandRejected,
    };

    private static string DefaultName(DateTimeOffset now) => now.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>The server's name and the backup's time, with nothing a file system would refuse.</summary>
    private static string DownloadName(Server server, Backup backup)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var name = new string([.. server.Name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        return $"{(name.Length == 0 ? server.ShortId : name)}-{backup.CreatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip";
    }

    internal static BackupView View(Backup b) => new(b.Id.Value, b.Name, b.Status, b.SizeBytes, b.FileCount, b.SkippedFiles,
        b.Sha256 is { } hash ? Convert.ToHexStringLower(hash) : null, b.IsLocked, b.CreatedVia, b.CreatedAt, b.CompletedAt, b.Duration?.TotalSeconds,
        b.ErrorMessage, b.OperationId?.Value, b.RemoteStatus, b.RemoteError, b.CopyMode, b.SnapshotError, b.GameSave, HasGameDatabase: b.HasGameDatabase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention on server {ServerId} stopped at backup {BackupId}; it continues after the next backup")]
    private partial void LogPruneStopped(ServerId serverId, BackupId backupId);
}
