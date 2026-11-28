using System.Formats.Tar;
using System.IO.Compression;

namespace Pulsar.Agent.Core.Updates;

/// <summary>
/// Unpacks a release's Linux tarball (its SHA-256 checked already) into a new folder: what is inside its one top folder
/// goes at the folder's top. Only folders and files are taken (a link, a device or anything else refuses the tarball),
/// every entry stays inside the folder, nothing is written twice, and the sizes are bounded as for the ZIP
/// (<see cref="UpdateArchive"/>). Files the tarball marks executable by their owner become 0755, the others 0644, and
/// folders 0755: nothing of its owners or other bits is kept.
/// </summary>
public static class UpdateTarball
{
    private const UnixFileMode Program = (UnixFileMode)0x1ED;
    private const UnixFileMode Plain = (UnixFileMode)0x1A4;

    public static void Extract(string archive, string destination, string top)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var prefix = root + Path.DirectorySeparatorChar;
        CreateFolder(root);
        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entries = 0;
        long total = 0;
        while (tar.GetNextEntry() is { } entry)
        {
            if (++entries > UpdateArchive.MaxEntries)
            {
                throw new InvalidDataException($"The release's tarball has more than {UpdateArchive.MaxEntries} entries.");
            }

            // A PAX header for the whole archive describes no file.
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
            {
                continue;
            }

            var name = entry.Name;
            if (name == top || name == top + "/")
            {
                if (entry.EntryType != TarEntryType.Directory)
                {
                    throw new InvalidDataException($"The release's tarball has {name} as something other than its folder.");
                }

                continue;
            }

            var relative = name.StartsWith(top + "/", StringComparison.Ordinal) ? name[(top.Length + 1)..] : null;
            var parts = relative?.TrimEnd('/').Split('/');
            if (relative is null || parts is null || parts.Any(p => p is "" or "." or "..") || relative.Contains('\\', StringComparison.Ordinal)
                || relative.Contains('\0', StringComparison.Ordinal) || (OperatingSystem.IsWindows() && relative.Contains(':', StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"The release's tarball has an entry outside its folder {top}: {name}");
            }

            var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The release's tarball has an entry outside its folder {top}: {name}");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    CreateFolder(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    total += entry.Length;
                    if (entry.Length < 0 || total > UpdateArchive.MaxBytes)
                    {
                        throw new InvalidDataException($"The release's tarball unpacks to more than {UpdateArchive.MaxBytes} bytes.");
                    }

                    CreateFolder(Path.GetDirectoryName(path)!);
                    Write(entry, path);
                    break;
                default:
                    throw new InvalidDataException($"The release's tarball has a {entry.EntryType} entry ({name}): only folders and files are taken.");
            }
        }
    }

    private static void Write(TarEntry entry, string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = (entry.Mode & UnixFileMode.UserExecute) != 0 ? Program : Plain;
        }

        using var output = new FileStream(path, options);
        if (entry.DataStream is not { } input)
        {
            return;
        }

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > entry.Length)
            {
                throw new InvalidDataException($"{entry.Name} in the release's tarball is larger than it says.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static void CreateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, Program);
        }
    }
}
