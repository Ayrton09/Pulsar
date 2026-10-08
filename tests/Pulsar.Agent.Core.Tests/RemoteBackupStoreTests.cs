using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>An S3-compatible server for tests: PULSAR_TEST_S3 = "endpoint;access key;secret key" (CONTRIBUTING.md).</summary>
public static class TestS3
{
    private static readonly string[]? s_parts = Environment.GetEnvironmentVariable("PULSAR_TEST_S3")?.Split(';');

    public static bool IsEnabled => s_parts is { Length: 3 };

    public static RemoteBackupTarget Target(string bucket, string key, string? secret = null) =>
        new(s_parts![0], "us-east-1", bucket, key, s_parts[1], secret ?? s_parts[2], PathStyle: true);

    /// <summary>The tests' bucket (one for all of them: each bucket costs a test server space), created if missing.</summary>
    public static async Task<string> BucketAsync()
    {
        const string bucket = "pulsar-tests";
        using var client = new AmazonS3Client(new BasicAWSCredentials(s_parts![1], s_parts[2]),
            new AmazonS3Config { ServiceURL = s_parts[0], ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        try
        {
            await client.PutBucketAsync(bucket, TestContext.Current.CancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Made by an earlier test.
        }

        return bucket;
    }

    /// <summary>A key no other test uses.</summary>
    public static string NewKey() => $"tests/{Guid.NewGuid():N}.zip";
}

public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Set PULSAR_TEST_S3 to run the remote storage tests against an S3-compatible server.";
        SkipUnless = nameof(TestS3.IsEnabled);
        SkipType = typeof(TestS3);
    }
}

/// <summary>Backups copied to S3-compatible storage and brought back (Phase 10).</summary>
public sealed class RemoteBackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-remote-tests", Guid.NewGuid().ToString("N"));
    private readonly List<(string Stage, double? Percent)> _progress = [];

    public RemoteBackupStoreTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://s3.example.com")] // resolves to a public address (below)
    [InlineData("http://mixed.example.com")] // one private address, one public
    [InlineData("http://8.8.8.8:9000")]
    [InlineData("http://172.32.0.1")]
    [InlineData("http://[2001:db8::1]:9000")]
    [InlineData("ftp://s3.example.com")]
    [InlineData("not a url")]
    public async Task RemoteStorageIsReachedOverHttpsOrOnlyOnTheLocalNetwork(string endpoint)
    {
        // Backups carry the servers' files: they never travel in clear over the internet. Checked before anything is sent.
        var target = new RemoteBackupTarget(endpoint, "us-east-1", "bucket", "key.zip", "access", "secret", PathStyle: true);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var refused = await Assert.ThrowsAsync<BackupException>(() => new S3RemoteBackupStore(Resolve).DeleteAsync(target, cancelled.Token));

        Assert.Equal(BackupErrors.RemoteFailed, refused.Code);
        Assert.Contains("HTTPS", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameThatResolvesToNothingIsRefused()
    {
        var target = new RemoteBackupTarget("http://gone.example.com", "us-east-1", "bucket", "key.zip", "access", "secret", PathStyle: true);

        var refused = await Assert.ThrowsAsync<BackupException>(() => new S3RemoteBackupStore(Resolve).DeleteAsync(target, Ct));

        Assert.Equal(BackupErrors.RemoteFailed, refused.Code);
        Assert.Contains("gone.example.com", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:{0}")]
    [InlineData("http://nas.home.lan:{0}")] // resolved once, to 127.0.0.1, and connected to that address
    public async Task PlainHttpReachesStorageOnTheLocalNetwork(string endpoint)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = ServeOneNoContentAsync(listener);
        var target = new RemoteBackupTarget(string.Format(CultureInfo.InvariantCulture, endpoint, port), "us-east-1", "bucket", "key.zip",
            "access", "secret", PathStyle: true);

        await new S3RemoteBackupStore(Resolve).DeleteAsync(target, Ct);

        Assert.StartsWith("DELETE /bucket/key.zip ", await served, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void PrivateAddressesAreTheLoopbackPrivateAndLinkLocalRanges(string address, bool expected) =>
        Assert.Equal(expected, S3RemoteBackupStore.IsPrivateAddress(IPAddress.Parse(address)));

    private static Task<IPAddress[]> Resolve(string host, CancellationToken cancellationToken) => Task.FromResult(host switch
    {
        "s3.example.com" => [IPAddress.Parse("93.184.216.34")],
        "mixed.example.com" => [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("93.184.216.34")],
        "nas.home.lan" => [IPAddress.Loopback],
        _ => Array.Empty<IPAddress>(),
    });

    /// <summary>Answers one request with 204 (what S3 says to a delete); returns the request line.</summary>
    private static async Task<string> ServeOneNoContentAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync(Ct);
        var stream = client.GetStream();
        var request = new StringBuilder();
        var buffer = new byte[4096];
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, Ct);
            if (read == 0)
            {
                break;
            }

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), Ct);
        return request.ToString();
    }

    [S3Fact]
    public async Task AnArchiveGoesToTheBucketAndComesBackTheSame()
    {
        var target = TestS3.Target(await TestS3.BucketAsync(), TestS3.NewKey());
        var file = Path.Combine(_root, "archive.zip");
        var content = RandomNumberGenerator.GetBytes(40 * 1024 * 1024 + 123); // more than two upload parts
        await File.WriteAllBytesAsync(file, content, Ct);
        var store = new S3RemoteBackupStore();
        var percents = new List<double>();

        await store.UploadAsync(target, file, "sha", p =>
        {
            lock (percents)
            {
                percents.Add(p);
            }

            return Task.CompletedTask;
        }, Ct);

        Assert.Contains(100, percents);
        var copy = Path.Combine(_root, "copy.zip");
        await store.DownloadAsync(target, copy, _ => Task.CompletedTask, Ct);
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(await File.ReadAllBytesAsync(copy, Ct)));

        var opened = await store.OpenAsync(target, Ct);
        Assert.Equal(content.Length, opened.Length);
        await using (opened.Content)
        {
            using var read = new MemoryStream();
            await opened.Content.CopyToAsync(read, Ct);
            Assert.Equal(content.Length, read.Length);
        }

        await store.DeleteAsync(target, Ct);
        await store.DeleteAsync(target, Ct); // already gone: fine
        var gone = await Assert.ThrowsAsync<BackupException>(() => store.DownloadAsync(target, copy, _ => Task.CompletedTask, Ct));
        Assert.Equal(BackupErrors.RemoteFailed, gone.Code);
        Assert.Equal("The backup's copy is not in remote storage.", gone.Message);
    }

    [S3Fact]
    public async Task WrongCredentialsSayWhatRemoteStorageAnswered()
    {
        var bucket = await TestS3.BucketAsync();
        var file = Path.Combine(_root, "archive.zip");
        await File.WriteAllTextAsync(file, "zip", Ct);
        var store = new S3RemoteBackupStore();

        var refused = await Assert.ThrowsAsync<BackupException>(() =>
            store.UploadAsync(TestS3.Target(bucket, TestS3.NewKey(), secret: "not the secret"), file, "sha", _ => Task.CompletedTask, Ct));
        Assert.Equal(BackupErrors.RemoteFailed, refused.Code);
        Assert.DoesNotContain("not the secret", refused.Message, StringComparison.Ordinal);
        // (A bucket that does not exist is not checked here: some servers, SeaweedFS among them, create it on the first upload.)
    }

    [Fact]
    public void TheSecretNeverShowsInATargetsText()
    {
        var target = new RemoteBackupTarget("https://s3.example.com", "auto", "bucket", "key", "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG", PathStyle: true);

        Assert.DoesNotContain("wJalrXUtnFEMI", target.ToString(), StringComparison.Ordinal);
        Assert.Contains("AKIDEXAMPLE", target.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCopyLeavesTheBackupOnTheNodeAndSaysWhy()
    {
        var store = new BackupStore(new DirectServerFileManager(), NullLogger<BackupStore>.Instance, new FakeRemote { Fail = "Remote storage refused the credentials or the bucket (AccessDenied)." });
        var location = new BackupLocation(_root, "abcd1234", Guid.NewGuid());
        Directory.CreateDirectory(location.Directory);
        await File.WriteAllTextAsync(location.Archive, "zip", Ct);
        var made = new BackupResult(3, 1, 0, "sha");

        var result = await store.CopyToRemoteAsync(location, made, Target(), Progress, Ct);

        Assert.False(result.RemoteCopied);
        Assert.Equal("Remote storage refused the credentials or the bucket (AccessDenied).", result.RemoteError);
        Assert.True(File.Exists(location.Archive));
        Assert.Equal(BackupStages.Uploading, _progress[0].Stage);
    }

    [Fact]
    public async Task ACancelledCopyLeavesNothing()
    {
        var store = new BackupStore(new DirectServerFileManager(), NullLogger<BackupStore>.Instance, new FakeRemote { Cancel = true });
        var location = new BackupLocation(_root, "abcd1234", Guid.NewGuid());
        Directory.CreateDirectory(location.Directory);
        await File.WriteAllTextAsync(location.Archive, "zip", Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CopyToRemoteAsync(location, new BackupResult(3, 1, 0, "sha"), Target(), Progress, Ct));

        Assert.False(File.Exists(location.Archive));
    }

    [Fact]
    public async Task AFetchedCopyMustBeTheArchiveTheNodeMade()
    {
        var content = "the archive"u8.ToArray();
        var remote = new FakeRemote { Content = content };
        var store = new BackupStore(new DirectServerFileManager(), NullLogger<BackupStore>.Instance, remote);
        var location = new BackupLocation(_root, "abcd1234", Guid.NewGuid());

        var wrong = await Assert.ThrowsAsync<BackupException>(() => store.FetchAsync(location, Target(), new string('0', 64), Progress, Ct));
        Assert.Equal(BackupErrors.Corrupt, wrong.Code);
        Assert.False(File.Exists(location.Archive));
        Assert.False(File.Exists(location.Partial));

        await store.FetchAsync(location, Target(), Convert.ToHexStringLower(SHA256.HashData(content)), Progress, Ct);
        Assert.Equal(content, await File.ReadAllBytesAsync(location.Archive, Ct));
        Assert.Contains(_progress, p => p.Stage == BackupStages.Downloading);

        // The node has it again: nothing is downloaded.
        remote.Downloads = 0;
        await store.FetchAsync(location, Target(), Convert.ToHexStringLower(SHA256.HashData(content)), Progress, Ct);
        Assert.Equal(0, remote.Downloads);
    }

    private static RemoteBackupTarget Target() => new(null, "us-east-1", "bucket", "key", "id", "secret", PathStyle: false);

    private Task Progress(string stage, double? percent)
    {
        lock (_progress)
        {
            _progress.Add((stage, percent));
        }

        return Task.CompletedTask;
    }

    private sealed class FakeRemote : IRemoteBackupStore
    {
        public string? Fail { get; init; }

        public bool Cancel { get; init; }

        public byte[] Content { get; init; } = [];

        public int Downloads { get; set; }

        public Task UploadAsync(RemoteBackupTarget target, string file, string sha256, Func<double, Task> progress, CancellationToken cancellationToken) =>
            Cancel ? throw new OperationCanceledException()
            : Fail is { } reason ? throw new BackupException(BackupErrors.RemoteFailed, reason)
            : progress(100);

        public async Task DownloadAsync(RemoteBackupTarget target, string file, Func<double, Task> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            await File.WriteAllBytesAsync(file, Content, cancellationToken);
            await progress(100);
        }

        public Task<RemoteBackup> OpenAsync(RemoteBackupTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteBackup(new MemoryStream(Content), Content.Length, DateTimeOffset.UnixEpoch));

        public Task DeleteAsync(RemoteBackupTarget target, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
