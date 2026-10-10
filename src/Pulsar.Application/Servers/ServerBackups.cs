using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
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
    GameSaveResult? GameSave);

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
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock,
    ILogger<ServerBackups> logger)
{
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
    public async Task<Result<BackupView>> CreateAsync(ServerId id, string? name, OperationRequester via, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (Connection(server.NodeId, AgentCapabilities.CreateBackup) is { IsSuccess: false } connection)
        {
            return connection.Error!;
        }

        var now = clock.GetUtcNow();
        var requestedBy = via == OperationRequester.User ? request.UserId : null;
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, requestedBy, via, now);
        var created = Backup.Create(server, string.IsNullOrWhiteSpace(name) ? DefaultName(now) : name, operation, requestedBy, via, now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var backup = created.Value;
        var remote = await RemoteForNewAsync(server, backup, cancellationToken);
        var run = await operations.RunAsync(server, operation, new CreateBackupCommand(server.Id.Value, backup.Id.Value, remote), () =>
        {
            db.Backups.Add(backup);
            audit.Record(AuditActions.Server.BackupRequested, metadata: new { backup = backup.Id.Value, backup.Name })
                .WithServer(server.Id, server.NodeId);
        }, cancellationToken);
        if (!run.IsSuccess)
        {
            return run.Error!;
        }

        if (run.Value.Status == OperationStatus.Failed)
        {
            // The node did not take it: the backup will never be made.
            backup.Ended(BackupStatus.Failed, run.Value.ErrorMessage ?? run.Value.ErrorCode, clock.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await db.Entry(backup).ReloadAsync(cancellationToken);
        return View(backup);
    }

    /// <summary>
    /// Puts the backup's files back (the server must be stopped); the server cannot start until it ends. A
    /// <paramref name="clean"/> restore first deletes the files the backup's rules select that it does not have.
    /// </summary>
    public async Task<Result> RestoreAsync(ServerId id, BackupId backupId, bool clean, CancellationToken cancellationToken)
    {
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

        var operation = Operation.Create(OperationType.BackupRestore, server.NodeId, server.Id, request.UserId, OperationRequester.User, clock.GetUtcNow());
        var remote = await RemoteOfAsync(backup, cancellationToken);
        var run = await operations.RunAsync(server, operation,
            new RestoreBackupCommand(server.Id.Value, backup.Id.Value, Convert.ToHexStringLower(sha256), remote, clean),
            () => audit.Record(AuditActions.Server.BackupRestoreRequested, metadata: new { backup = backup.Id.Value, backup.Name, clean })
                .WithServer(server.Id, server.NodeId),
            cancellationToken);
        return run.IsSuccess ? Result.Success() : run.Error!;
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

    public async Task<Result<BackupView>> SetLockedAsync(ServerId id, BackupId backupId, bool locked, CancellationToken cancellationToken)
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
        await db.SaveChangesAsync(cancellationToken);
        return View(backup);
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

    /// <summary>
    /// Retention: deletes the oldest complete, unlocked backups beyond the server's limit. Runs after every new backup
    /// and when the limit changes; whatever it cannot delete now (the node is disconnected) goes at the next run.
    /// </summary>
    public async Task PruneAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server?.BackupLimit is not { } limit)
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
            if (!(await RemoveAsync(server, backup, AuditActor.System, reason: "retention", cancellationToken)).IsSuccess)
            {
                LogPruneStopped(server.Id, backup.Id);
                return;
            }
        }
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

        var download = transfers.BeginDownload(server.NodeId);
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
        catch (TimeoutException)
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

    /// <summary>Deletes the archive on the node (complete backups only have one), then the row.</summary>
    private async Task<Result> RemoveAsync(Server server, Backup backup, AuditActor actor, string? reason, CancellationToken cancellationToken)
    {
        if (backup.Status == BackupStatus.Completed)
        {
            if (Connection(server.NodeId, AgentCapabilities.DeleteBackup) is { IsSuccess: false } connection)
            {
                return connection.Error!;
            }

            var remote = await RemoteOfAsync(backup, cancellationToken);
            var ack = Ack(await connections.SendAsync(server.NodeId, new DeleteBackupCommand(server.Id.Value, backup.Id.Value, remote),
                actor.UserId, cancellationToken));
            if (!ack.IsSuccess)
            {
                return ack.Error!;
            }
        }

        db.Backups.Remove(backup);
        audit.Record(AuditActions.Server.BackupDeleted, AuditOutcome.Success, actor, new { backup = backup.Id.Value, backup.Name, reason })
            .WithServer(server.Id, server.NodeId);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted (or locked) by someone else meanwhile: the archive is gone either way.
            db.ChangeTracker.Clear();
        }

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
        return new BackupList([.. backups.Select(View)], server.BackupLimit, server.BackupStorageId?.Value, options);
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
        b.ErrorMessage, b.OperationId?.Value, b.RemoteStatus, b.RemoteError, b.CopyMode, b.SnapshotError, b.GameSave);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention on server {ServerId} stopped at backup {BackupId}; it continues after the next backup")]
    private partial void LogPruneStopped(ServerId serverId, BackupId backupId);
}
