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
        ErrorMessage = errorMessage is { Length: > MaxErrorMessageLength } ? errorMessage[..MaxErrorMessageLength] : errorMessage;
        CompletedAt = now;
    }

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
