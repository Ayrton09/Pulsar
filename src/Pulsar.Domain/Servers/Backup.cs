using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

public enum BackupStatus
{
    /// <summary>The node is making it (its operation runs).</summary>
    Creating,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>The backup's copy in remote storage (<see cref="BackupStorage"/>).</summary>
public enum RemoteCopyStatus
{
    /// <summary>No remote storage for this backup.</summary>
    None,

    /// <summary>The node uploads it after making the backup.</summary>
    Pending,
    Copied,

    /// <summary>The upload failed; the backup is on the node only.</summary>
    Failed,
}

/// <summary>How the node read the server's files for a backup (Phase 15).</summary>
public enum BackupCopyMode
{
    /// <summary>The game was not running: nothing was writing the files.</summary>
    Stopped,

    /// <summary>From a snapshot of the volume: every file as it was at the same moment.</summary>
    Snapshot,

    /// <summary>Live, while the game ran: files it wrote meanwhile may be half-written, files it held open were left out.</summary>
    Live,
}

/// <summary>How a running game answered when asked to save before its backup.</summary>
public enum GameSaveResult
{
    Confirmed,

    /// <summary>The game has no line that confirms a save: the node waited the time its manifest gives.</summary>
    Waited,

    /// <summary>The game did not confirm in time.</summary>
    Unconfirmed,
}

/// <summary>
/// A backup of a server (ADR-0012; docs/architecture/02, "Backups"): metadata only, the zip lives on the server's node.
/// Made by a <see cref="OperationType.BackupCreate"/> operation; locked backups are never deleted by retention.
/// </summary>
public sealed class Backup
{
    public const int MaxNameLength = 128;
    public const int MaxErrorMessageLength = 1024;
    public const int MaxSnapshotErrorLength = 64;

    private Backup()
    {
        // For EF Core materialization.
        Name = null!;
    }

    private Backup(BackupId id, ServerId serverId, NodeId nodeId, string name, OperationId operationId, UserId? createdBy, OperationRequester createdVia,
        DateTimeOffset now)
    {
        Id = id;
        ServerId = serverId;
        NodeId = nodeId;
        Name = name;
        Status = BackupStatus.Creating;
        OperationId = operationId;
        CreatedBy = createdBy;
        CreatedVia = createdVia;
        CreatedAt = now;
        StartedAt = now; // the operation is sent right away
    }

    public BackupId Id { get; private set; }

    public ServerId ServerId { get; private set; }

    public NodeId NodeId { get; private set; }

    public string Name { get; private set; }

    public BackupStatus Status { get; private set; }

    /// <summary>Bytes of the archive.</summary>
    public long? SizeBytes { get; private set; }

    public int? FileCount { get; private set; }

    /// <summary>Files left out because the game held them open.</summary>
    public int? SkippedFiles { get; private set; }

    /// <summary>The archive's SHA-256: a restore checks the node still has exactly this.</summary>
    public byte[]? Sha256 { get; private set; }

    public bool IsLocked { get; private set; }

    public UserId? CreatedBy { get; private set; }

    public OperationRequester CreatedVia { get; private set; }

    public OperationId? OperationId { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The storage its copy goes (or went) to; kept while the copy exists, so the storage cannot be deleted under it.</summary>
    public BackupStorageId? StorageId { get; private set; }

    /// <summary>The copy's object key in <see cref="StorageId"/>'s bucket.</summary>
    public string? RemoteKey { get; private set; }

    public RemoteCopyStatus RemoteStatus { get; private set; }

    public string? RemoteError { get; private set; }

    /// <summary>How the node read the files; null for backups made before Phase 15 (or by an older agent).</summary>
    public BackupCopyMode? CopyMode { get; private set; }

    /// <summary>Why a running game's files were read live rather than from a snapshot (a <c>snapshot.*</c> code).</summary>
    public string? SnapshotError { get; private set; }

    /// <summary>How the game answered when asked to save first; null when it was not asked.</summary>
    public GameSaveResult? GameSave { get; private set; }

    /// <summary>How long the node took (from the request to its result).</summary>
    public TimeSpan? Duration => Status == BackupStatus.Completed && CompletedAt is { } end ? end - (StartedAt ?? CreatedAt) : null;

    public static Result<Backup> Create(Server server, string name, Operation operation, UserId? createdBy, OperationRequester createdVia,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(operation);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || !DisplayNames.IsDisplayable(trimmed))
        {
            return ServerErrors.BackupNameInvalid;
        }

        return new Backup(BackupId.New(), server.Id, server.NodeId, trimmed, operation.Id, createdBy, createdVia, now);
    }

    /// <summary>
    /// The copy of <paramref name="source"/>, another server's backup, that <paramref name="restore"/> fetches onto
    /// <paramref name="server"/>'s node and restores there (a clone, Pulsar 1.0.16): being made until the restore ends,
    /// with the source's archive's size, files and SHA-256 (the node checks it gets exactly that archive).
    /// </summary>
    public static Backup Receiving(Server server, Backup source, Operation restore, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(restore);
        if (source is not { Status: BackupStatus.Completed, Sha256: not null, SizeBytes: not null })
        {
            throw new ArgumentException("Only a completed backup is copied.", nameof(source));
        }

        return new Backup(BackupId.New(), server.Id, server.NodeId, source.Name, restore.Id, null, OperationRequester.System, now)
        {
            SizeBytes = source.SizeBytes,
            FileCount = source.FileCount,
            SkippedFiles = source.SkippedFiles,
            Sha256 = source.Sha256,
            CopyMode = source.CopyMode,
            GameSave = source.GameSave,
        };
    }

    /// <summary>How the restore that fetched it ended (<see cref="Receiving"/>): the archive is now on the node, or not.</summary>
    public void Received(bool succeeded, string? errorMessage, DateTimeOffset now)
    {
        if (Status != BackupStatus.Creating || Sha256 is null)
        {
            return;
        }

        Status = succeeded ? BackupStatus.Completed : BackupStatus.Failed;
        ErrorMessage = succeeded || errorMessage is null ? null : Truncate(errorMessage);
        CompletedAt = now;
    }

    /// <summary>The node copies this backup to <paramref name="storage"/> once made.</summary>
    public void CopyTo(BackupStorage storage, string serverShortId)
    {
        ArgumentNullException.ThrowIfNull(storage);
        StorageId = storage.Id;
        RemoteKey = storage.KeyFor(serverShortId, Id);
        RemoteStatus = RemoteCopyStatus.Pending;
    }

    /// <summary>The copy could not even be asked for (the node's agent cannot upload): the backup is on the node only.</summary>
    public void CopyNotPossible(string reason)
    {
        StorageId = null;
        RemoteKey = null;
        RemoteStatus = RemoteCopyStatus.Failed;
        RemoteError = Truncate(reason);
    }

    /// <summary>How the upload the node made after the backup went.</summary>
    public void RemoteCopyEnded(bool copied, string? error)
    {
        if (RemoteStatus != RemoteCopyStatus.Pending)
        {
            return;
        }

        RemoteStatus = copied ? RemoteCopyStatus.Copied : RemoteCopyStatus.Failed;
        RemoteError = copied ? null : Truncate(error ?? "The node did not copy the backup.");
        if (!copied)
        {
            StorageId = null; // nothing there to keep the storage for
            RemoteKey = null;
        }
    }

    /// <param name="snapshotError">Kept only when the files were read live, and only a plain code (the agent's word, shown as a reason).</param>
    public void Completed(long size, int fileCount, int skippedFiles, byte[] sha256, DateTimeOffset now, BackupCopyMode? copyMode = null,
        string? snapshotError = null, GameSaveResult? gameSave = null)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (Status != BackupStatus.Creating)
        {
            return;
        }

        Status = BackupStatus.Completed;
        SizeBytes = size;
        FileCount = fileCount;
        SkippedFiles = skippedFiles;
        Sha256 = sha256;
        CompletedAt = now;
        CopyMode = copyMode;
        SnapshotError = copyMode == BackupCopyMode.Live && IsCode(snapshotError) ? snapshotError : null;
        GameSave = gameSave;
    }

    /// <summary>
    /// A clean restore deletes the files the backup's rules select that it does not have: files the backup left out
    /// because the game held them open would be deleted too, so a backup that skipped any cannot be restored clean.
    /// </summary>
    public Result CheckCleanRestore() => SkippedFiles is > 0 ? ServerErrors.CleanRestoreIncomplete : Result.Success();

    private static bool IsCode(string? code) => code is { Length: > 0 and <= MaxSnapshotErrorLength }
        && code.All(c => c is (>= 'a' and <= 'z') or '.' or '_');

    /// <summary>The backup could not be made (or was cancelled); later reports change nothing.</summary>
    public void Ended(BackupStatus status, string? errorMessage, DateTimeOffset now)
    {
        if (status is not (BackupStatus.Failed or BackupStatus.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Not an unsuccessful end.");
        }

        if (Status != BackupStatus.Creating)
        {
            return;
        }

        Status = status;
        ErrorMessage = errorMessage is null ? null : Truncate(errorMessage);
        CompletedAt = now;
        if (RemoteStatus == RemoteCopyStatus.Pending)
        {
            RemoteStatus = RemoteCopyStatus.None;
            StorageId = null;
            RemoteKey = null;
        }
    }

    private static string Truncate(string text) => TextLimits.Truncate(text, MaxErrorMessageLength);

    /// <summary>A locked backup is kept until it is unlocked: retention skips it and deleting it is refused.</summary>
    public Result SetLocked(bool locked)
    {
        if (Status != BackupStatus.Completed)
        {
            return ServerErrors.BackupNotCompleted;
        }

        IsLocked = locked;
        return Result.Success();
    }
}
