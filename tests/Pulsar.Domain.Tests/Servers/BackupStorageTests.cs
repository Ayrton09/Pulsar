using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Servers;

public sealed class BackupStorageTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] s_secret = [1, 2, 3];

    [Fact]
    public void AStorageNormalizesItsSettingsAndKeepsItsSecretWhenNotChanged()
    {
        var storage = Storage(endpoint: " https://abc.r2.cloudflarestorage.com/ ", prefix: "/pulsar/backups/");

        Assert.Equal("https://abc.r2.cloudflarestorage.com", storage.Endpoint);
        Assert.Equal("pulsar/backups/", storage.Prefix);
        var backup = BackupId.New();
        Assert.Equal($"pulsar/backups/k3x7m2qa/{backup.Value:N}.zip", storage.KeyFor("k3x7m2qa", backup));

        // A new name, region, path style or folder for new copies keeps it.
        Assert.True(storage.Change("R2", "https://abc.r2.cloudflarestorage.com", "us-east-1", "pulsar-backups", null, "AKIDEXAMPLE", null, false, s_now)
            .IsSuccess);
        Assert.Equal(string.Empty, storage.Prefix);
        Assert.Equal(s_secret, storage.SecretProtected);

        // Another endpoint, bucket or key: the saved secret is never sent there, so it is asked for again.
        foreach (var (endpoint, bucket, key) in new[]
        {
            ((string?)null, "pulsar-backups", "AKIDEXAMPLE"),
            ("https://abc.r2.cloudflarestorage.com", "someone-elses", "AKIDEXAMPLE"),
            ("https://abc.r2.cloudflarestorage.com", "pulsar-backups", "AKIDOTHER"),
        })
        {
            Assert.Equal(BackupStorageErrors.SecretRequired, storage.Change("R2", endpoint, "us-east-1", bucket, null, key, null, false, s_now).Error);
        }

        byte[] typed = [7, 8, 9];
        Assert.True(storage.Change("R2", null, "us-east-1", "bucket", null, "AKID", typed, false, s_now).IsSuccess);
        Assert.Null(storage.Endpoint); // AWS S3
        Assert.Equal(typed, storage.SecretProtected);
    }

    [Theory]
    [InlineData("ab", false)]
    [InlineData("my-bucket", true)]
    [InlineData("my.bucket.2026", true)]
    [InlineData("My-Bucket", false)]
    [InlineData("-bucket", false)]
    [InlineData("bucket-", false)]
    [InlineData("bu..cket", false)]
    [InlineData("bucket_name", false)]
    public void BucketNamesFollowS3Rules(string name, bool valid) => Assert.Equal(valid, BackupStorage.IsBucketName(name));

    [Fact]
    public void SettingsAreChecked()
    {
        Assert.Equal(BackupStorageErrors.SecretRequired,
            BackupStorage.Create("x", null, "us-east-1", "bucket", null, "AKID", null, false, s_now).Error);
        Assert.Equal(BackupStorageErrors.NameInvalid, Create(name: " ").Error);
        Assert.Equal(BackupStorageErrors.EndpointInvalid, Create(endpoint: "ftp://s3.example.com").Error);
        Assert.Equal(BackupStorageErrors.EndpointInvalid, Create(endpoint: "https://user:pw@s3.example.com").Error);
        Assert.Equal(BackupStorageErrors.EndpointInvalid, Create(endpoint: "https://s3.example.com/bucket").Error);
        Assert.Equal(BackupStorageErrors.RegionInvalid, Create(region: "US East").Error);
        Assert.Equal(BackupStorageErrors.BucketInvalid, Create(bucket: "No").Error);
        Assert.Equal(BackupStorageErrors.PrefixInvalid, Create(prefix: "a/../b").Error);
        Assert.Equal(BackupStorageErrors.PrefixInvalid, Create(prefix: "a b").Error);
        Assert.Equal(BackupStorageErrors.AccessKeyInvalid, Create(accessKey: "AK ID").Error);
    }

    [Theory]
    [InlineData("https://8.8.8.8", true)]
    [InlineData("http://127.0.0.1:9000", true)]
    [InlineData("http://10.0.0.5:9000", true)]
    [InlineData("http://172.16.0.1", true)]
    [InlineData("http://172.31.255.254", true)]
    [InlineData("http://192.168.1.20:9000", true)]
    [InlineData("http://169.254.10.1", true)]
    [InlineData("http://[::1]:9000", true)]
    [InlineData("http://[fd12:3456::1]", true)]
    [InlineData("http://[fe80::1]", true)]
    [InlineData("http://[::ffff:192.168.1.20]", true)]
    [InlineData("http://nas.home.lan:9000", true)] // a name: the node checks what it resolves to
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("http://11.0.0.1", false)]
    [InlineData("http://[2001:db8::1]", false)]
    [InlineData("http://[::ffff:8.8.8.8]", false)]
    public void PlainHttpIsOnlyForPrivateAddresses(string endpoint, bool valid)
    {
        var result = Create(endpoint: endpoint);

        Assert.Equal(valid, result.IsSuccess);
        if (!valid)
        {
            Assert.Equal(BackupStorageErrors.EndpointNeedsHttps, result.Error);
        }
    }

    [Fact]
    public void AStorageHoldingCopiesKeepsItsEndpointAndBucket()
    {
        var storage = Storage(prefix: "pulsar");

        // Copies are found at the storage's endpoint and bucket, under the key each backup kept (its prefix included).
        Assert.Equal(BackupStorageErrors.HoldsCopies,
            storage.Change("S3", "https://other.example.com", "auto", "pulsar-backups", "pulsar", "AKID", null, true, s_now, holdsCopies: true).Error);
        Assert.Equal(BackupStorageErrors.HoldsCopies,
            storage.Change("S3", "https://s3.example.com", "auto", "other-bucket", "pulsar", "AKID", null, true, s_now, holdsCopies: true).Error);
        Assert.Equal(BackupStorageErrors.HoldsCopies,
            storage.Change("S3", null, "auto", "pulsar-backups", "pulsar", "AKID", null, true, s_now, holdsCopies: true).Error);
        Assert.Equal(("https://s3.example.com", "pulsar-backups"), (storage.Endpoint, storage.Bucket));

        // New credentials, region, path style or folder for new copies: the same bucket.
        byte[] rotated = [4, 5, 6];
        Assert.True(storage.Change("Renamed", " HTTPS://S3.example.com/ ", "us-east-1", "pulsar-backups", "new-folder", "AKID2", rotated, false, s_now,
            holdsCopies: true).IsSuccess);
        Assert.Equal(("AKID2", rotated, "new-folder/"), (storage.AccessKeyId, storage.SecretProtected, storage.Prefix));

        // Without copies, it may move (with its secret typed again).
        Assert.True(storage.Change("Moved", "https://other.example.com", "auto", "other-bucket", null, "AKID2", rotated, true, s_now).IsSuccess);
    }

    [Fact]
    public void ABackupsCopyIsPendingThenCopiedOrFailed()
    {
        var storage = Storage();
        var copied = NewBackup();
        copied.CopyTo(storage, "k3x7m2qa");
        Assert.Equal(RemoteCopyStatus.Pending, copied.RemoteStatus);
        Assert.Equal(storage.Id, copied.StorageId);
        copied.Completed(10, 1, 0, new byte[32], s_now);
        copied.RemoteCopyEnded(copied: true, null);
        Assert.Equal(RemoteCopyStatus.Copied, copied.RemoteStatus);
        Assert.Equal(storage.KeyFor("k3x7m2qa", copied.Id), copied.RemoteKey);

        var failed = NewBackup();
        failed.CopyTo(storage, "k3x7m2qa");
        failed.Completed(10, 1, 0, new byte[32], s_now);
        failed.RemoteCopyEnded(copied: false, "Remote storage refused the credentials or the bucket (AccessDenied).");
        Assert.Equal(RemoteCopyStatus.Failed, failed.RemoteStatus);
        Assert.Null(failed.StorageId); // nothing there: the storage is free to go
        Assert.Equal("Remote storage refused the credentials or the bucket (AccessDenied).", failed.RemoteError);

        var notMade = NewBackup();
        notMade.CopyTo(storage, "k3x7m2qa");
        notMade.Ended(BackupStatus.Failed, "disk full", s_now);
        Assert.Equal(RemoteCopyStatus.None, notMade.RemoteStatus);
        Assert.Null(notMade.RemoteKey);
    }

    private static Backup NewBackup()
    {
        var server = Server.Create(NodeId.New(), TemplateId.New(), "Test", null, Server.NewShortId(), s_now).Value;
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        return Backup.Create(server, "Backup", operation, null, OperationRequester.User, s_now).Value;
    }

    private static BackupStorage Storage(string? endpoint = "https://s3.example.com", string? prefix = null) =>
        Assert.IsType<BackupStorage>(Create(endpoint: endpoint, prefix: prefix).Value);

    private static Result<BackupStorage> Create(string name = "Storage", string? endpoint = "https://s3.example.com", string region = "auto",
        string bucket = "pulsar-backups", string? prefix = null, string accessKey = "AKIDEXAMPLE") =>
        BackupStorage.Create(name, endpoint, region, bucket, prefix, accessKey, s_secret, pathStyle: true, s_now);
}
