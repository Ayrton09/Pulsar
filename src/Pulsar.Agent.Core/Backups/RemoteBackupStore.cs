using System.Net;
using System.Net.Sockets;
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

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public S3RemoteBackupStore()
        : this(Dns.GetHostAddressesAsync)
    {
    }

    /// <summary>For tests: how host names are resolved.</summary>
    internal S3RemoteBackupStore(Func<string, CancellationToken, Task<IPAddress[]>> resolve) => _resolve = resolve;

    /// <summary>Loopback (127/8, ::1), private (10/8, 172.16/12, 192.168/16, fc00::/7) or link-local (169.254/16, fe80::/10).</summary>
    public static bool IsPrivateAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 10 or 127
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (IPAddress.IsLoopback(address) || address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal);
    }

    public async Task UploadAsync(RemoteBackupTarget target, string file, string sha256, Func<double, Task> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(progress);
        using var client = await ClientAsync(target, cancellationToken);
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
        using var client = await ClientAsync(target, cancellationToken);
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
        var client = await ClientAsync(target, cancellationToken);
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
        using var client = await ClientAsync(target, cancellationToken);
        await CallAsync(() => client.DeleteObjectAsync(target.Bucket, target.Key, cancellationToken), cancellationToken);
    }

    private async Task<AmazonS3Client> ClientAsync(RemoteBackupTarget target, CancellationToken cancellationToken)
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
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new BackupException(BackupErrors.RemoteFailed, "Remote storage must be reached over HTTPS.");
            }

            if (uri.Scheme == Uri.UriSchemeHttp)
            {
                // Backups carry the servers' files (and the credentials sign requests): plain HTTP only to this machine or
                // the local network (storage at home reached by its LAN address). A name is resolved once, every address
                // must be private, and the client connects to those addresses only: a later answer cannot point it elsewhere.
                if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                {
                    if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IsPrivateAddress(address))
                    {
                        throw PublicHttp($"{uri.Host} is a public address");
                    }
                }
                else
                {
                    var addresses = await ResolveAsync(uri.IdnHost, cancellationToken);
                    if (addresses.FirstOrDefault(a => !IsPrivateAddress(a)) is { } open)
                    {
                        throw PublicHttp($"{uri.Host} resolves to a public address ({open})");
                    }

                    config.HttpClientFactory = new PinnedHttpClientFactory(addresses);
                }
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

    private static BackupException PublicHttp(string why) =>
        new(BackupErrors.RemoteFailed,
            $"Remote storage must be reached over HTTPS: plain HTTP is only accepted to this machine or the local network, and {why}.");

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await _resolve(host, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new BackupException(BackupErrors.RemoteFailed, $"Remote storage could not be reached: {host} could not be resolved ({ex.Message}).", ex);
        }

        return addresses.Length > 0
            ? addresses
            : throw new BackupException(BackupErrors.RemoteFailed, $"Remote storage could not be reached: {host} could not be resolved.");
    }

    /// <summary>HTTP clients that connect only to the addresses checked when the S3 client was made.</summary>
    private sealed class PinnedHttpClientFactory(IPAddress[] addresses) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // The checked addresses are connected to directly: a proxy would make the callback dial its port instead.
                UseProxy = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            return new HttpClient(handler) { Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(100) };
        }

        // Cached with this S3 client only: another storage's client must not reuse these addresses.
        public override string? GetConfigUniqueString(IClientConfig clientConfig) => null;
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
