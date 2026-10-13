using System.Collections.Concurrent;
using Pulsar.Agent.Core.Backups;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>Remote storage in memory for the in-process agent: objects by bucket and key, and the secrets it was given.</summary>
public sealed class MemoryRemoteBackups : IRemoteBackupStore
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public ConcurrentBag<string> SecretsSeen { get; } = [];

    /// <summary>The size limit each download was given (the backup's size, from the panel's restore command).</summary>
    public ConcurrentBag<long?> MaxBytesSeen { get; } = [];

    /// <summary>Uploads fail with this, as remote storage answers.</summary>
    public string? FailUploads { get; set; }

    public static string Name(RemoteBackupTarget target) => $"{target.Bucket}/{target.Key}";

    public async Task UploadAsync(RemoteBackupTarget target, string file, string sha256, Func<double, Task> progress, CancellationToken cancellationToken)
    {
        SecretsSeen.Add(target.SecretAccessKey);
        if (FailUploads is { } reason)
        {
            throw new BackupException(BackupErrors.RemoteFailed, reason);
        }

        Objects[Name(target)] = await File.ReadAllBytesAsync(file, cancellationToken);
        await progress(100);
    }

    public async Task DownloadAsync(RemoteBackupTarget target, string file, long? maxBytes, Func<double, Task> progress, CancellationToken cancellationToken)
    {
        MaxBytesSeen.Add(maxBytes);
        await File.WriteAllBytesAsync(file, Get(target), cancellationToken);
        await progress(100);
    }

    public Task<RemoteBackup> OpenAsync(RemoteBackupTarget target, CancellationToken cancellationToken)
    {
        var content = Get(target);
        return Task.FromResult(new RemoteBackup(new MemoryStream(content), content.Length, DateTimeOffset.UtcNow));
    }

    public Task DeleteAsync(RemoteBackupTarget target, CancellationToken cancellationToken)
    {
        Objects.TryRemove(Name(target), out _);
        return Task.CompletedTask;
    }

    private byte[] Get(RemoteBackupTarget target) => Objects.TryGetValue(Name(target), out var content)
        ? content
        : throw new BackupException(BackupErrors.RemoteFailed, "The backup's copy is not in remote storage.");
}
