using System.Formats.Tar;
using System.IO.Compression;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// A downloaded archive the agent unpacks into a server's folder (an addon's, a release's, a runtime's): its files
/// (folders left out), then their contents, by their place in that list. A zip, a .tar.gz, or on a Linux node a zip
/// whose only file is a .tar at its root (TShock's Linux builds, packed so to keep their programs' execute bits), read
/// as that tar.
/// </summary>
internal abstract class ServerArchive : IDisposable
{
    /// <summary>Each file, its length and whether it is a program (an execute bit in a tar, which Linux sets on the copy).</summary>
    public abstract IReadOnlyList<(string Name, long Length, bool Program)> Files();

    public abstract void Extract(Action<int, Stream> write);

    public abstract void Dispose();

    public static bool IsTarGz(string name) =>
        name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    /// <param name="download">The verified download (a file: a tar is read twice).</param>
    /// <param name="name">The download's file name, which tells a .tar.gz.</param>
    /// <param name="within">Only this folder of a zip, unpacked as its root; all of it when null or empty.</param>
    /// <param name="maximumRead">The most a tar may decompress to, headers and skipped data included.</param>
    /// <param name="maximumEntries">The most entries a tar may have, folders included.</param>
    /// <param name="refused">The error for a tar that is too large or has an entry the agent does not unpack.</param>
    /// <param name="leftOut">
    /// The tar's symbolic links that are left out, never written (a Java runtime's links between its license texts);
    /// any other link is refused, as without it.
    /// </param>
    /// <param name="tarInZip">A zip whose only file is a .tar at its root is that tar (Linux nodes); otherwise a zip as any other.</param>
    public static ServerArchive Open(Stream download, string name, string? within, long maximumRead, int maximumEntries, Func<string, Exception> refused,
        Func<string, bool>? leftOut = null, bool tarInZip = false)
    {
        if (IsTarGz(name))
        {
            return new TarServerArchive(() => new GZipStream(download, CompressionMode.Decompress, leaveOpen: true), download, name, maximumRead,
                maximumEntries, refused, leftOut);
        }

        var zip = new ZipServerArchive(download, within);
        if (!tarInZip || !string.IsNullOrEmpty(within) || zip.SingleTar() is not { } tar)
        {
            return zip;
        }

        return new TarServerArchive(() => BoundedEntryStream.Open(tar), download, name, maximumRead, maximumEntries, refused, leftOut, zip);
    }
}

/// <param name="within">Only the files of this folder of the zip, named from it; all of them when null or empty.</param>
internal sealed class ZipServerArchive(Stream download, string? within = null) : ServerArchive
{
    private readonly ZipArchive _zip = new(download, ZipArchiveMode.Read, leaveOpen: true);

    private readonly string? _prefix = string.IsNullOrEmpty(within) ? null : within + "/";

    public override IReadOnlyList<(string Name, long Length, bool Program)> Files() =>
        [.. FileEntries().Select(e => (_prefix is null ? e.FullName : e.FullName.Replace('\\', '/')[_prefix.Length..], e.Length, false))];

    /// <summary>The zip's only file when it is a .tar at its root; null otherwise.</summary>
    public ZipArchiveEntry? SingleTar() =>
        FileEntries().Take(2).ToList() is [var only] && only.FullName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
            && only.FullName.IndexOfAny(['/', '\\']) < 0
            ? only
            : null;

    public override void Extract(Action<int, Stream> write)
    {
        var index = 0;
        foreach (var entry in FileEntries())
        {
            using var content = BoundedEntryStream.Open(entry);
            write(index++, content);
        }
    }

    public override void Dispose() => _zip.Dispose();

    private IEnumerable<ZipArchiveEntry> FileEntries() => _zip.Entries.Where(e => e.FullName.Replace('\\', '/') is var name && !name.EndsWith('/')
        && (_prefix is null || (name.StartsWith(_prefix, StringComparison.Ordinal) && name.Length > _prefix.Length)));
}

/// <summary>
/// A tar (a .tar.gz, or a .tar in a zip), read twice from the verified download: once for its files, which are checked
/// before anything is written, once to write them. Only plain files and folders: a link, a device or anything else fails
/// it, and a file's content ends where its header says. What either reading decompresses is counted, headers and
/// skipped data included: past <c>maximumRead</c>, the archive is refused (a small download can hold far more).
/// </summary>
/// <param name="open">Opens the tar's bytes from their start (the gzip's decompression, or the zip's entry).</param>
/// <param name="leftOut">The symbolic links (by their names in the tar) that are skipped instead of refused.</param>
/// <param name="zip">The zip the tar is in, disposed with it.</param>
internal sealed class TarServerArchive(Func<Stream> open, Stream download, string name, long maximumRead, int maximumEntries,
    Func<string, Exception> refused, Func<string, bool>? leftOut = null, ServerArchive? zip = null) : ServerArchive
{
    public override IReadOnlyList<(string Name, long Length, bool Program)> Files()
    {
        var files = new List<(string Name, long Length, bool Program)>();
        Read(entry => files.Add((Name(entry), entry.Length, (entry.Mode & UnixFileMode.UserExecute) != 0)));
        return files;
    }

    public override void Extract(Action<int, Stream> write)
    {
        var index = 0;
        Read(entry => write(index++, entry.DataStream ?? Stream.Null));
    }

    public override void Dispose() => zip?.Dispose();

    private void Read(Action<TarEntry> file)
    {
        download.Seek(0, SeekOrigin.Begin);
        using var source = open();
        using var counted = new BoundedReadStream(source, maximumRead, () => refused($"{name} is larger than Pulsar unpacks."));
        using var reader = new TarReader(counted, leaveOpen: true);
        var entries = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            if (++entries > maximumEntries)
            {
                throw refused($"{name} is larger than Pulsar unpacks.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    file(entry);
                    break;
                case TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes:
                    break;
                case TarEntryType.SymbolicLink when leftOut?.Invoke(Name(entry)) == true:
                    break; // not written: nothing it points at is reached
                default:
                    throw refused($"{name} has an entry Pulsar does not unpack: '{entry.Name}' ({entry.EntryType}).");
            }
        }
    }

    // Tar names often start with ./ (a folder packed from inside it).
    private static string Name(TarEntry entry) => entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;

    /// <summary>Reads another stream up to a number of bytes in all; reading further throws what <c>tooLarge</c> makes.</summary>
    private sealed class BoundedReadStream(Stream inner, long maximum, Func<Exception> tooLarge) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _read += read;
            return _read > maximum ? throw tooLarge() : read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
