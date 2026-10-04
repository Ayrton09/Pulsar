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

/// <summary>
/// A backup of a server (ADR-0012; docs/architecture/02, "Backups"): metadata only, the zip lives on the server's node.
/// Made by a <see cref="OperationType.BackupCreate"/> operation; locked backups are never deleted by retention.
/// </summary>
public sealed class Backup
{
    public const int MaxNameLength = 128;
    public const int MaxErrorMessageLength = 1024;

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

    /// <summary>How long the node took (from the request to its result).</summary>
    public TimeSpan? Duration => Status == BackupStatus.Completed && CompletedAt is { } end ? end - (StartedAt ?? CreatedAt) : null;

    public static Result<Backup> Create(Server server, string name, Operation operation, UserId? createdBy, OperationRequester createdVia,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(operation);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            return ServerErrors.BackupNameInvalid;
        }

        return new Backup(BackupId.New(), server.Id, server.NodeId, trimmed, operation.Id, createdBy, createdVia, now);
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

    public void Completed(long size, int fileCount, int skippedFiles, byte[] sha256, DateTimeOffset now)
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
    }

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

    private static string Truncate(string text) => text.Length > MaxErrorMessageLength ? text[..MaxErrorMessageLength] : text;

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
