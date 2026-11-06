using System.Formats.Tar;
using System.IO.Compression;

namespace Pulsar.ReleasePacker;

/// <summary>
/// Packs a release folder into the Linux tarball (scripts/release/Build-Release.ps1): a gzip'd tar whose one top entry is
/// the folder, as root owns it on Linux (uid and gid 0), directories 0755, files 0644 and programs 0755 (ELF files and
/// shell scripts), with every entry dated at the commit's time and in ordinal order, so the same tree packs the same
/// bytes. Windows' tar cannot give an entry Unix permissions, and the release is built on Windows. Links are refused:
/// install-pulsar.sh refuses them too.
/// </summary>
internal static class TarPacker
{
    public const UnixFileMode DirectoryMode = (UnixFileMode)0b111_101_101;
    public const UnixFileMode FileMode = (UnixFileMode)0b110_100_100;
    public const UnixFileMode ProgramMode = (UnixFileMode)0b111_101_101;

    private static readonly byte[] s_elfMagic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];

    public static void Pack(string folder, string output, DateTimeOffset modified)
    {
        var root = new DirectoryInfo(Path.GetFullPath(folder));
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"{root.FullName} does not exist.");
        }

        if (root.LinkTarget is not null)
        {
            throw new InvalidOperationException($"{root.FullName} is a link: a release folder is packed as it is, never through one.");
        }

        using var file = new FileStream(output, System.IO.FileMode.CreateNew, FileAccess.Write);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        Add(tar, root, root.Name, modified);
    }

    /// <summary>Whether the file is a program on Linux: an ELF file, or a shell script.</summary>
    public static bool IsProgram(FileInfo file)
    {
        if (file.Extension.Equals(".sh", StringComparison.Ordinal))
        {
            return true;
        }

        Span<byte> head = stackalloc byte[4];
        using var stream = file.OpenRead();
        return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head.SequenceEqual(s_elfMagic);
    }

    private static void Add(TarWriter tar, DirectoryInfo directory, string name, DateTimeOffset modified)
    {
        tar.WriteEntry(Entry(TarEntryType.Directory, name + "/", DirectoryMode, modified));
        foreach (var item in directory.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            if (item.LinkTarget is not null)
            {
                throw new InvalidOperationException($"{item.FullName} is a link: a release holds none.");
            }

            var path = name + "/" + item.Name;
            if (item is DirectoryInfo child)
            {
                Add(tar, child, path, modified);
                continue;
            }

            var info = (FileInfo)item;
            var entry = Entry(TarEntryType.RegularFile, path, IsProgram(info) ? ProgramMode : FileMode, modified);
            using var content = info.OpenRead();
            entry.DataStream = content;
            tar.WriteEntry(entry);
        }
    }

    private static PaxTarEntry Entry(TarEntryType type, string name, UnixFileMode mode, DateTimeOffset modified)
    {
        // Every time the entry carries is the commit's: left out, a pax entry would get the packing's access and change times.
        var time = modified.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(type, name, new Dictionary<string, string> { ["mtime"] = time, ["atime"] = time, ["ctime"] = time })
        {
            Mode = mode,
            Uid = 0,
            Gid = 0,
            UserName = "root",
            GroupName = "root",
            ModificationTime = modified,
        };
    }
}
