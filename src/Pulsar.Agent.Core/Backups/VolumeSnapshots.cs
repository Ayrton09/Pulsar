using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Backups;

/// <summary>
/// Read-only snapshots of the volume a server directory is on (Windows: the Volume Shadow Copy service): a backup of a
/// running game reads every file as it was at the same moment, files the game holds open included
/// (docs/architecture/06, "Backup snapshots").
/// </summary>
public interface IVolumeSnapshots
{
    /// <summary>Takes a snapshot of the volume <paramref name="directory"/> is on.</summary>
    /// <exception cref="SnapshotException">No snapshot could be taken; its code says why.</exception>
    Task<IVolumeSnapshot> TakeAsync(string directory, CancellationToken cancellationToken);
}

/// <summary>A snapshot taken for one backup; disposing it deletes it.</summary>
public interface IVolumeSnapshot : IAsyncDisposable
{
    /// <summary>The directory the snapshot was taken for, as it was then (read-only).</summary>
    string Directory { get; }
}

public sealed class SnapshotException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>One of <see cref="BackupSnapshotErrors"/>.</summary>
    public string Code { get; } = code;
}

/// <summary>A system without snapshots (tests, other platforms): every backup of a running game reads it live.</summary>
public sealed class NoVolumeSnapshots(string code = BackupSnapshotErrors.Unsupported) : IVolumeSnapshots
{
    public Task<IVolumeSnapshot> TakeAsync(string directory, CancellationToken cancellationToken) =>
        Task.FromException<IVolumeSnapshot>(new SnapshotException(code, "This system takes no snapshots."));
}
