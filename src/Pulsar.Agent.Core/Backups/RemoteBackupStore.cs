using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Backups;

/// <summary>A backup's copy in remote storage, opened to be read.</summary>
public sealed record RemoteBackup(Stream Content, long Length, DateTimeOffset Modified);

/// <summary>
/// Backups copied to S3-compatible storage (Phase 10). Every call gets its target, credentials included, from the panel's
/// command: nothing about the storage is kept on the node. Failures are <see cref="BackupException"/>s with
/// <see cref="BackupErrors.RemoteFailed"/> and what the storage answered (never the credentials).
/// </summary>
public interface IRemoteBackupStore
{
    Task UploadAsync(RemoteBackupTarget target, string file, string sha256, Func<double, Task> progress, CancellationToken cancellationToken);

    Task DownloadAsync(RemoteBackupTarget target, string file, Func<double, Task> progress, CancellationToken cancellationToken);

    Task<RemoteBackup> OpenAsync(RemoteBackupTarget target, CancellationToken cancellationToken);

    /// <summary>Deletes the copy; one that is already gone is fine.</summary>
    Task DeleteAsync(RemoteBackupTarget target, CancellationToken cancellationToken);
}

/// <summary><see cref="IRemoteBackupStore"/> with the AWS SDK, which also speaks to R2, B2, MinIO, SeaweedFS and others.</summary>
public sealed class S3RemoteBackupStore : IRemoteBackupStore
{
    /// <summary>Kept with the object: what the node made, so a copy can be told apart from its archive.</summary>
    public const string Sha256Metadata = "pulsar-sha256";

    private const long PartSize = 16 * 1024 * 1024;

    public async Task UploadAsync(RemoteBackupTarget target, string file, string sha256, Func<double, Task> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(progress);
        using var client = Client(target);
        using var transfer = new TransferUtility(client, new TransferUtilityConfig { ConcurrentServiceRequests = 4 });
        var request = new TransferUtilityUploadRequest
        {
            BucketName = target.Bucket,
            Key = target.Key,
            FilePath = file,
            PartSize = PartSize,
            ContentType = "application/zip",
        };
        request.Metadata.Add(Sha256Metadata, sha256);
        var reported = -1;
        request.UploadProgressEvent += (_, e) =>
        {
            // Called on the SDK's threads; a whole percent at a time is plenty.
            if (e.PercentDone > Volatile.Read(ref reported))
            {
                Volatile.Write(ref reported, e.PercentDone);
                _ = progress(e.PercentDone);
            }
        };
        await CallAsync(() => transfer.UploadAsync(request, cancellationToken), cancellationToken);
        // The SDK raises its events on its own threads, the last one maybe after the upload returns: the end is said here.
        await progress(100);
    }

    public async Task DownloadAsync(RemoteBackupTarget target, string file, Func<double, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(progress);
        using var client = Client(target);
        using var response = await CallAsync(() => client.GetObjectAsync(target.Bucket, target.Key, cancellationToken), cancellationToken);
        var reported = -1;
        response.WriteObjectProgressEvent += (_, e) =>
        {
            if (e.PercentDone > Volatile.Read(ref reported))
            {
                Volatile.Write(ref reported, e.PercentDone);
                _ = progress(e.PercentDone);
            }
        };
        await CallAsync(() => response.WriteResponseStreamToFileAsync(file, append: false, cancellationToken), cancellationToken);
        await progress(100);
    }

    public async Task<RemoteBackup> OpenAsync(RemoteBackupTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var client = Client(target);
        try
        {
            var response = await CallAsync(() => client.GetObjectAsync(target.Bucket, target.Key, cancellationToken), cancellationToken);
            return new RemoteBackup(new OwningStream(response.ResponseStream, response.ContentLength, response, client), response.ContentLength,
                response.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UnixEpoch);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task DeleteAsync(RemoteBackupTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var client = Client(target);
        await CallAsync(() => client.DeleteObjectAsync(target.Bucket, target.Key, cancellationToken), cancellationToken);
    }

    private static AmazonS3Client Client(RemoteBackupTarget target)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = target.PathStyle,
            // Checksums only where S3 requires them: several S3-compatible services refuse the SDK's default trailers.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 4,
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (target.Endpoint is { } endpoint)
        {
            // Backups carry the servers' files (and the credentials sign requests): plain HTTP only to this machine.
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            {
                throw new BackupException(BackupErrors.RemoteFailed,
                    "Remote storage must be reached over HTTPS (plain HTTP is only accepted for storage on this machine).");
            }

            config.ServiceURL = endpoint;
            config.AuthenticationRegion = target.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(target.Region);
        }

        return new AmazonS3Client(new BasicAWSCredentials(target.AccessKeyId, target.SecretAccessKey), config);
    }

    private static async Task CallAsync(Func<Task> call, CancellationToken cancellationToken) =>
        await CallAsync(async () =>
        {
            await call();
            return true;
        }, cancellationToken);

    private static async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (AmazonS3Exception ex)
        {
            throw new BackupException(BackupErrors.RemoteFailed, ex.StatusCode switch
            {
                HttpStatusCode.NotFound when ex.ErrorCode == "NoSuchKey" => "The backup's copy is not in remote storage.",
                HttpStatusCode.NotFound => $"Remote storage has no bucket by that name ({ex.ErrorCode}).",
                HttpStatusCode.Forbidden => $"Remote storage refused the credentials or the bucket ({ex.ErrorCode}).",
                _ => $"Remote storage answered {(int)ex.StatusCode} {ex.ErrorCode}: {ex.Message}",
            }, ex);
        }
        catch (AmazonServiceException ex)
        {
            throw new BackupException(BackupErrors.RemoteFailed, $"Remote storage could not be reached: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new BackupException(BackupErrors.RemoteFailed, $"Remote storage could not be reached: {ex.Message}", ex);
        }
    }

    /// <summary>The object's content; disposing it releases the response and the client.</summary>
    private sealed class OwningStream(Stream inner, long length, IDisposable response, IDisposable client) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
