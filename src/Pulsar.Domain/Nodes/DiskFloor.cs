namespace Pulsar.Domain.Nodes;

/// <summary>
/// The free space the panel keeps on a node's drives (docs/architecture/08, "Disk space"): it refuses backups and
/// uploads that would leave less than <see cref="MinFreePercent"/> % or <see cref="MinFreeBytes"/> free, as the agent's
/// last metrics tell. A game filling the drive itself is not stopped: the node notification says so.
/// </summary>
public static class DiskFloor
{
    public const int MinFreePercent = 5;

    public const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether a drive of <paramref name="totalBytes"/> with <paramref name="freeBytes"/> free keeps the floor once
    /// <paramref name="adding"/> more bytes are written to it (a drive of unknown size does).
    /// </summary>
    public static bool Keeps(long totalBytes, long freeBytes, long adding = 0)
    {
        var left = freeBytes - Math.Max(adding, 0);
        return totalBytes <= 0 || (left >= MinFreeBytes && left * 100.0 / totalBytes >= MinFreePercent);
    }
}
