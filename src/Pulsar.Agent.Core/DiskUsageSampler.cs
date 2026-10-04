using Pulsar.Contracts;

namespace Pulsar.Agent.Core;

/// <summary>The drives holding the servers and backups folders, with their free space (the panel warns when one is nearly full).</summary>
public static class DiskUsageSampler
{
    public static IReadOnlyList<DiskUsage> Sample(string serversRoot, string backupsRoot)
    {
        var uses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, use) in new[] { (serversRoot, DiskUses.Servers), (backupsRoot, DiskUses.Backups) })
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root))
            {
                (uses.TryGetValue(root, out var list) ? list : uses[root] = []).Add(use);
            }
        }

        List<DiskUsage> disks = [];
        foreach (var (root, used) in uses)
        {
            try
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    disks.Add(new DiskUsage(drive.Name, drive.TotalSize, drive.AvailableFreeSpace, used));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A network share or a drive gone: nothing to report about it.
            }
        }

        return disks;
    }
}
