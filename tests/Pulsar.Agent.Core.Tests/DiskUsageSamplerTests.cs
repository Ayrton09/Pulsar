using Pulsar.Agent.Core;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

public sealed class DiskUsageSamplerTests
{
    [Fact]
    public void FoldersOnTheSameDriveAreOneDiskWithBothUses()
    {
        var root = Path.Combine(Path.GetTempPath(), "pulsar-disks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var servers = Directory.CreateDirectory(Path.Combine(root, "servers")).FullName;
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;

            var disk = Assert.Single(DiskUsageSampler.Sample(servers, backups));

            Assert.Equal(Path.GetPathRoot(servers), disk.Root, StringComparer.OrdinalIgnoreCase);
            Assert.Equal([DiskUses.Servers, DiskUses.Backups], disk.Uses);
            Assert.True(disk.TotalBytes > 0);
            Assert.InRange(disk.FreeBytes, 0, disk.TotalBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AFolderThatDoesNotExistYetStillCountsItsDrive() =>
        Assert.Single(DiskUsageSampler.Sample(Path.Combine(Path.GetTempPath(), "not-yet", "servers"), Path.Combine(Path.GetTempPath(), "not-yet", "backups")));
}
