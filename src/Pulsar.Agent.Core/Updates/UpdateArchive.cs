using System.IO.Compression;

namespace Pulsar.Agent.Core.Updates;

/// <summary>
/// Unpacks a release's ZIP (its SHA-256 checked already) into a new folder: every entry inside that folder, no more
/// entries or bytes than a release holds, and no entry larger than it says (the copy stops at its declared size).
/// </summary>
public static class UpdateArchive
{
    public const int MaxEntries = 10_000;
    public const long MaxBytes = 2L << 30;

    public static void Extract(string archive, string destination)
    {
        var root = Path.GetFullPath(destination);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"The release's ZIP has more than {MaxEntries} entries.");
        }

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            // Windows PowerShell's Compress-Archive writes backslashes; either separator is one.
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':', StringComparison.Ordinal) || name.Split('/').Any(p => p is ".." or "."))
            {
                throw new InvalidDataException($"The release's ZIP has an entry outside its folder: {entry.FullName}");
            }

            var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The release's ZIP has an entry outside its folder: {entry.FullName}");
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }

            total += entry.Length;
            if (entry.Length < 0 || total > MaxBytes)
            {
                throw new InvalidDataException($"The release's ZIP unpacks to more than {MaxBytes} bytes.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > entry.Length)
                {
                    throw new InvalidDataException($"{entry.FullName} in the release's ZIP is larger than it says.");
                }

                output.Write(buffer, 0, read);
            }
        }
    }
}
