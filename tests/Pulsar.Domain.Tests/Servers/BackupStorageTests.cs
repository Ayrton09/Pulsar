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

        Assert.True(storage.Change("R2", null, "us-east-1", "bucket", null, "AKID", null, false, s_now).IsSuccess);
        Assert.Null(storage.Endpoint); // AWS S3
        Assert.Equal(string.Empty, storage.Prefix);
        Assert.Equal(s_secret, storage.SecretProtected);
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
