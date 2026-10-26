using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Files;

/// <summary>Stages of the file manager's zips, as reported in <see cref="OperationProgress"/>.</summary>
public static class ArchiveStages
{
    public const string Scanning = BackupStages.Scanning;
    public const string Archiving = BackupStages.Archiving;
    public const string Checking = BackupStages.Checking;
    public const string Extracting = "extracting";
}

/// <summary>
/// The file manager's zips (Pulsar 1.0.11; docs/architecture/08, "File manager"): files and folders of a server zipped
/// into a new zip of the server, and a zip of the server written out into one of its folders. Everything is read and
/// written through the <see cref="IServerFileManager"/>, as its other commands are: never through a link, and on Windows
/// as the server's account, which is what the zip's files belong to. Pulsar's own folder is never read into a zip nor
/// written from one.
/// </summary>
public sealed partial class FileArchives(IServerFileManager files, ILogger<FileArchives> logger)
{
    /// <summary>The most entries a zip may have to be written out.</summary>
    public const int MaxEntries = 200_000;

    /// <summary>Room left on the server's drive once a zip is made or written out.</summary>
    public const long FreeSpaceMargin = 1024L * 1024 * 1024;

    /// <summary>
    /// The most of a zip, from its central directory to its end, that is read to list its entries: .NET holds all of it in
    /// memory before a single entry can be looked at (a legitimate zip of <see cref="MaxEntries"/> entries takes a few MiB).
    /// </summary>
    public const long MaxDirectoryBytes = 48L * 1024 * 1024;

    private const int BufferSize = 256 * 1024;
    private const int SampleSize = 256 * 1024;
    private const int MaxLoggedSkips = 20;
    private static readonly TimeSpan s_progressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Zips <paramref name="names"/> of <paramref name="directory"/> into <paramref name="archive"/>, which must not exist:
    /// each under its own name, a folder with everything below it. Links are never followed and files the game holds open
    /// are skipped and counted; the zip appears complete or not at all.
    /// </summary>
    public async Task<ArchiveResult> CompressAsync(ProvisionedServer server, string directory, IReadOnlyList<string> names, string archive,
        BackupProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(progress);
        var selected = Selection(directory, names, archive);
        await progress(ArchiveStages.Scanning, null);
        var walk = files.Walk(server);
        var chosen = new List<TreeEntry>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in walk)
        {
            if (selected.FirstOrDefault(s => entry.Path.Equals(s, StringComparison.OrdinalIgnoreCase) || ServerFilePath.IsInside(entry.Path, s)) is not { } top)
            {
                continue;
            }

            found.Add(top);
            // Uploads still on their way are not files yet, and a link is never followed (nor kept: a zip has no links).
            if (entry.Kind == FileEntryKind.Link || ServerFilePath.Name(entry.Path).StartsWith(ServerFileRules.TemporaryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            chosen.Add(entry);
        }

        if (selected.FirstOrDefault(s => !found.Contains(s)) is { } missing)
        {
            throw new FileManagerException(FileErrors.NotFound, $"'{missing}' does not exist.");
        }

        var total = chosen.Where(e => e.Kind == FileEntryKind.File).Sum(e => e.Size);
        EnsureSpace(server, total);
        var prefix = directory.Length == 0 ? 0 : directory.Length + 1;
        // What the walk could not name or reach under the selection (a name made through extended paths, a folder too deep).
        var skipped = walk.RefusedUnder(selected);
        if (skipped > 0)
        {
            LogUnnameable(skipped, string.Join(", ", walk.RefusedSample.Select(BackupStore.Printable)));
        }

        var count = 0;
        using var pending = files.Create(server, archive, Bound(total, chosen.Count), overwrite: false, expectedVersion: null, sizeIsLimit: true);
        var meter = new Meter(ArchiveStages.Archiving, total, progress);
        await meter.ReportAsync(force: true);
        await using (var zip = await ZipArchive.CreateAsync(pending.Content, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken))
        {
            var buffer = new byte[BufferSize];
            foreach (var entry in chosen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.Path[prefix..];
                if (entry.Kind == FileEntryKind.Directory)
                {
                    zip.CreateEntry(name + "/").LastWriteTime = ZipTime(entry.ModifiedAt);
                    continue;
                }

                OpenedFile opened;
                try
                {
                    opened = files.OpenRead(server, entry.Path, long.MaxValue);
                }
                catch (FileManagerException ex)
                {
                    // Held open by the game, gone or turned into a link since the walk: the rest is still worth zipping.
                    if (++skipped <= MaxLoggedSkips)
                    {
                        LogSkipped(entry.Path, ex.Code);
                    }

                    meter.Add(entry.Size);
                    continue;
                }

                using (opened)
                {
                    // At most the size the walk listed: a file that grows as fast as it is read (a log) would otherwise
                    // keep the zip going until the disk is full.
                    var sample = (int)Math.Min(SampleSize, entry.Size);
                    int sampled;
                    try
                    {
                        sampled = await opened.Content.ReadAtLeastAsync(buffer.AsMemory(0, sample), sample, throwOnEndOfStream: false, cancellationToken);
                    }
                    catch (IOException ex) when (ServerFileRules.Translate(entry.Path, ex) is { Code: FileErrors.InUse })
                    {
                        // Opened, but locked by the game (Minecraft's session.lock): left out, as a file it holds open.
                        if (++skipped <= MaxLoggedSkips)
                        {
                            LogSkipped(entry.Path, FileErrors.InUse);
                        }

                        meter.Add(entry.Size);
                        continue;
                    }

                    var level = BackupStore.Shrinks(buffer.AsSpan(0, sampled)) ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                    var zipped = zip.CreateEntry(name, level);
                    zipped.LastWriteTime = ZipTime(opened.Info.Entry.ModifiedAt);
                    await using var target = await zipped.OpenAsync(cancellationToken);
                    await target.WriteAsync(buffer.AsMemory(0, sampled), cancellationToken);
                    long copied = sampled;
                    meter.Add(sampled);
                    int read;
                    try
                    {
                        while (copied < entry.Size
                            && (read = await opened.Content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, entry.Size - copied)), cancellationToken)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            copied += read;
                            meter.Add(read);
                            await meter.ReportAsync();
                        }
                    }
                    catch (IOException ex) when (ServerFileRules.Translate(entry.Path, ex) is { } failed)
                    {
                        // Locked halfway (its first bytes are in the zip already): the zip fails, saying which file.
                        throw failed;
                    }

                    meter.Add(entry.Size - copied); // a file that shrank since the walk
                }

                count++;
            }
        }

        await pending.Content.FlushAsync(cancellationToken);
        var made = pending.Commit();
        meter.Complete();
        await meter.ReportAsync(force: true);
        LogCompressed(archive, count, skipped, made.Entry.Size);
        return new ArchiveResult(made.Entry.Size, count, skipped);
    }

    /// <summary>
    /// Writes <paramref name="archive"/>'s files into <paramref name="destination"/> (created when missing). Every entry is
    /// checked before anything is written: a plain path below the destination (<c>\</c> read as <c>/</c>), not in Pulsar's
    /// own folder, once by any case, at most <see cref="MaxEntries"/>, all of them fitting on the drive; without
    /// <paramref name="overwrite"/>, no file the destination has. An entry longer than it declares is damaged: the files
    /// written before it stay.
    /// </summary>
    public async Task<ArchiveResult> ExtractAsync(ProvisionedServer server, string archive, string destination, bool overwrite, BackupProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(progress);
        Check(archive);
        if (destination.Length > 0)
        {
            Check(destination);
        }

        await progress(ArchiveStages.Checking, null);
        using var opened = files.OpenRead(server, archive, long.MaxValue);
        try
        {
            await CheckDirectoryAsync(opened.Content, cancellationToken);
            await using var zip = await ZipArchive.CreateAsync(opened.Content, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken);
            var entries = Entries(zip, destination);
            long total = 0;
            foreach (var (entry, path, directory) in entries)
            {
                if (!directory && (entry.Length < 0 || entry.Length > long.MaxValue / 2 - total))
                {
                    throw Invalid(path, "it says it holds more than any drive");
                }

                total += directory ? 0 : entry.Length;
            }

            EnsureSpace(server, total);
            if (!overwrite && entries.FirstOrDefault(e => !e.Directory && Exists(server, e.Path)) is { Path: { } taken })
            {
                throw new FileManagerException(FileErrors.Exists, $"'{taken}' already exists: nothing was written.");
            }

            if (destination.Length > 0)
            {
                files.EnsureDirectory(server, destination);
            }
            else if (ShortNamesElsewhere(server, entries.Select(e => e.Path)) is [var elsewhere, ..])
            {
                throw new FileManagerException(FileErrors.Exists,
                    $"'{elsewhere}' is the short name of another folder of the server here (Pulsar's own, say): nothing was written.");
            }

            var meter = new Meter(ArchiveStages.Extracting, total, progress);
            await meter.ReportAsync(force: true);
            var buffer = new byte[BufferSize];
            var count = 0;
            foreach (var (entry, path, directory) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directory)
                {
                    files.EnsureDirectory(server, path);
                    continue;
                }

                if (ServerFilePath.Parent(path) is { Length: > 0 } parent)
                {
                    files.EnsureDirectory(server, parent);
                }

                using var pending = files.Create(server, path, entry.Length, overwrite, expectedVersion: null);
                var crc = new Crc32();
                await using (var source = await entry.OpenAsync(cancellationToken))
                {
                    var remaining = entry.Length;
                    while (remaining > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0)
                        {
                            throw Invalid(path, "it is shorter than it says");
                        }

                        crc.Append(buffer.AsSpan(0, read));
                        await pending.Content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        remaining -= read;
                        meter.Add(read);
                        await meter.ReportAsync();
                    }

                    // A zip bomb declares little and inflates to much: what it declares is all it gets (.NET stops
                    // there by itself), and the checksum then tells the rest was cut, as it tells a damaged entry.
                    if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0)
                    {
                        throw Invalid(path, "it is longer than it says");
                    }

                    if (crc.Value != entry.Crc32)
                    {
                        throw Invalid(path, "its content does not match its checksum (damaged, or longer than it says)");
                    }
                }

                pending.Commit();
                count++;
            }

            meter.Complete();
            await meter.ReportAsync(force: true);
            LogExtracted(archive, destination, count);
            return new ArchiveResult(opened.Info.Entry.Size, count);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new FileManagerException(FileErrors.ArchiveInvalid, $"'{archive}' cannot be written out: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Every entry's path in the server, checked before anything is written. Entries written with <c>\</c> (PowerShell
    /// 5's <c>Compress-Archive</c>) are read with <c>/</c>.
    /// </summary>
    private static List<(ZipArchiveEntry Entry, string Path, bool Directory)> Entries(ZipArchive zip, string destination)
    {
        if (zip.Entries.Count > MaxEntries)
        {
            throw new FileManagerException(FileErrors.ArchiveInvalid, $"The zip has more than {MaxEntries} entries.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry, string, bool)>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var relative = directory ? name[..^1] : name;
            if (ServerFilePath.Error(relative) is { } error)
            {
                throw Invalid(entry.FullName, error);
            }

            var path = ServerFilePath.Join(destination, relative);
            if (ServerFilePath.Error(path) is { } tooLong)
            {
                throw Invalid(entry.FullName, tooLong);
            }

            if (FileCommands.IsPulsarFolder(path) || FileCommands.IsSteamLogin(path))
            {
                throw Invalid(entry.FullName, "Pulsar's own files are never written from a zip");
            }

            if (!seen.Add(path))
            {
                throw Invalid(entry.FullName, "it appears twice");
            }

            entries.Add((entry, path, directory));
        }

        // A file named like a folder the zip also fills (a/b and a/b/c) would fail halfway: refused first.
        var filePaths = entries.Where(e => !e.Item3).Select(e => e.Item2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, path, _) in entries)
        {
            for (var parent = ServerFilePath.Parent(path); parent.Length > destination.Length; parent = ServerFilePath.Parent(parent))
            {
                if (filePaths.Contains(parent))
                {
                    throw Invalid(parent, "it is a file and a folder");
                }
            }
        }

        return entries;
    }

    /// <summary>The selected paths, checked: one folder's names, each once, outside Pulsar's folder, the zip not among them.</summary>
    private static List<string> Selection(string directory, IReadOnlyList<string> names, string archive)
    {
        if (directory.Length > 0)
        {
            Check(directory);
        }

        Check(archive);
        if (names.Count is 0 or > CompressFilesCommand.MaxNames)
        {
            throw new FileManagerException(FileErrors.InvalidPath, $"Between 1 and {CompressFilesCommand.MaxNames} names.");
        }

        var selected = new List<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('/', StringComparison.Ordinal))
            {
                throw new FileManagerException(FileErrors.InvalidPath, $"'{name}' is not a name in the folder.");
            }

            var path = ServerFilePath.Join(directory, name);
            Check(path);
            if (selected.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                throw new FileManagerException(FileErrors.InvalidPath, $"'{name}' is named twice.");
            }

            // A zip inside a selected folder is fine: the folder is walked before the zip exists.
            if (archive.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileManagerException(FileErrors.InvalidPath, "The zip cannot be one of the names it zips.");
            }

            selected.Add(path);
        }

        return selected;
    }

    /// <summary>
    /// Refuses a zip whose end record says it has more than <see cref="MaxEntries"/> entries, or whose central directory
    /// (read from where the record says it starts to the end of the file, as .NET reads it) is larger than
    /// <see cref="MaxDirectoryBytes"/>, before .NET reads any of it into memory. A zip without an end record is left
    /// to .NET, which refuses it.
    /// </summary>
    internal static async Task CheckDirectoryAsync(Stream zip, CancellationToken cancellationToken)
    {
        const int EndSize = 22;
        const int Zip64LocatorSize = 20;
        var length = zip.Length;
        var tail = new byte[(int)Math.Min(length, EndSize + ushort.MaxValue + Zip64LocatorSize)];
        zip.Position = length - tail.Length;
        await zip.ReadExactlyAsync(tail, cancellationToken);
        var end = -1;
        for (var i = tail.Length - EndSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50)
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            zip.Position = 0;
            return;
        }

        long entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long start = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        var zip64 = entries == ushort.MaxValue || start == uint.MaxValue || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4)) == ushort.MaxValue;
        if (zip64 && end >= Zip64LocatorSize && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end - Zip64LocatorSize)) == 0x07064b50)
        {
            var record = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(end - Zip64LocatorSize + 8));
            if (record < 0 || record > length - 56)
            {
                throw new FileManagerException(FileErrors.ArchiveInvalid, "The zip's end record points outside it.");
            }

            var header = new byte[56];
            zip.Position = record;
            await zip.ReadExactlyAsync(header, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x06064b50)
            {
                entries = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
                start = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48));
            }
        }

        if (entries is < 0 or > MaxEntries)
        {
            throw new FileManagerException(FileErrors.ArchiveInvalid, $"The zip has more than {MaxEntries} entries.");
        }

        if (start < 0 || start > length || length - start > MaxDirectoryBytes)
        {
            throw new FileManagerException(FileErrors.ArchiveInvalid, "The zip's list of entries is too large to read.");
        }

        zip.Position = 0;
    }

    /// <summary>A path the file manager would take, outside Pulsar's own folder.</summary>
    private static void Check(string path)
    {
        if (path.Length == 0)
        {
            throw new FileManagerException(FileErrors.InvalidPath, "Not the server's folder itself.");
        }

        if (ServerFilePath.Error(path) is { } error)
        {
            throw new FileManagerException(FileErrors.InvalidPath, $"'{path}': {error}");
        }

        if (FileCommands.IsPulsarFolder(path) || FileCommands.IsSteamLogin(path))
        {
            throw new FileManagerException(FileErrors.AccessDenied, "Only Pulsar changes its own folder.");
        }
    }

    private bool Exists(ProvisionedServer server, string path)
    {
        try
        {
            files.Stat(server, path);
            return true;
        }
        catch (FileManagerException ex) when (ex.Code is FileErrors.NotFound or FileErrors.NotADirectory)
        {
            return false;
        }
    }

    /// <summary>
    /// The first folders of <paramref name="paths"/> named like 8.3 short names (<c>DATA~1</c>) that lead to another
    /// folder of the server (on a volume that keeps short names, <c>PULSAR~1</c> may be <c>.pulsar</c>): such a folder is
    /// written only as a folder of that very name, one the server has or that is created now.
    /// </summary>
    private List<string> ShortNamesElsewhere(ProvisionedServer server, IEnumerable<string> paths)
    {
        var shortNamed = paths.Select(p => p.Split('/')[0]).Where(FileCommands.IsShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (shortNamed.Count == 0)
        {
            return [];
        }

        var named = files.List(server, ServerFilePath.Root).Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var elsewhere = new List<string>();
        foreach (var name in shortNamed.Where(n => !named.Contains(n)))
        {
            try
            {
                files.CreateDirectory(server, name);
            }
            catch (FileManagerException ex) when (ex.Code == FileErrors.Exists)
            {
                elsewhere.Add(name);
            }
        }

        return elsewhere;
    }

    private static void EnsureSpace(ProvisionedServer server, long needed)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(server.Directory));
        if (root is { Length: > 0 } && new DriveInfo(root).AvailableFreeSpace - needed < FreeSpaceMargin)
        {
            throw new FileManagerException(FileErrors.DiskFull, $"The files ({needed} bytes) do not fit on the drive with {FreeSpaceMargin / (1024 * 1024)} MiB to spare.");
        }
    }

    /// <summary>The most a zip of <paramref name="files"/> entries holding <paramref name="total"/> bytes takes: deflate gives up on what does not shrink.</summary>
    private static long Bound(long total, int files) => total + (total / 1000) + (files * 1024L) + (1024 * 1024);

    private static FileManagerException Invalid(string entry, string reason) =>
        new(FileErrors.ArchiveInvalid, $"The zip cannot be written out: entry '{entry}': {reason}.");

    /// <summary>Zip stores local times from 1980 to 2107.</summary>
    private static DateTimeOffset ZipTime(DateTimeOffset time)
    {
        var earliest = new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2107, 12, 30, 0, 0, 0, TimeSpan.Zero);
        return time < earliest ? earliest : time > latest ? latest : time;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Zipped {Count} files into {Archive} ({Size} bytes, {Skipped} skipped)")]
    private partial void LogCompressed(string archive, int count, int skipped, long size);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote {Count} files of {Archive} into '{Destination}'")]
    private partial void LogExtracted(string archive, string destination, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Left {Path} out of a zip: {Code}")]
    private partial void LogSkipped(string path, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Left {Count} entries no path can name out of a zip (first: {Sample})")]
    private partial void LogUnnameable(int count, string sample);

    /// <summary>How far a stage got, reported at most every half second.</summary>
    private sealed class Meter(string stage, long total, BackupProgress progress)
    {
        private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();
        private long _done;
        private bool _complete;

        public void Add(long bytes) => _done += bytes;

        public void Complete() => (_done, _complete) = (total, true);

        public async Task ReportAsync(bool force = false)
        {
            if (!force && _since.Elapsed < s_progressInterval)
            {
                return;
            }

            _since.Restart();
            var percent = total <= 0 ? (_complete ? 100 : 0) : Math.Clamp(_done * 100.0 / total, 0, 100);
            await progress(stage, Math.Round(percent, 1));
        }
    }
}
