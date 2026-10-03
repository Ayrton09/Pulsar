using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Backups;

/// <summary>Stages of backup operations, as reported in <see cref="OperationProgress"/>.</summary>
public static class BackupStages
{
    public const string Scanning = "scanning";
    public const string Archiving = "archiving";
    public const string Checking = "checking";
    public const string Restoring = "restoring";
}

/// <summary>Reports a stage and, when known, how far it got (0–100).</summary>
public delegate Task BackupProgress(string stage, double? percent);

public sealed class BackupException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>One of <see cref="BackupErrors"/>.</summary>
    public string Code { get; } = code;
}

/// <summary>Where a backup lives on the node: <c>&lt;backups root&gt;\&lt;shortId&gt;\&lt;backupId&gt;.zip</c>.</summary>
public sealed record BackupLocation(string Root, string ShortId, Guid BackupId)
{
    public string Directory
    {
        get
        {
            ServerShortId.Validate(ShortId);
            return Path.Combine(Root, ShortId);
        }
    }

    public string Archive => Path.Combine(Directory, $"{BackupId:N}.zip");

    /// <summary>The archive while it is being written; renamed to <see cref="Archive"/> once complete.</summary>
    public string Partial => Archive + PartialSuffix;

    internal const string PartialSuffix = ".partial";
}

/// <summary>
/// Zip backups of server directories, kept on the node (ADR-0012; docs/architecture/08, "Backups and restore"). Server
/// files are read and written only through the <see cref="IServerFileManager"/>: never through a link, and on Windows as
/// the server's account. The archives themselves live in the agent's own directory, out of the servers' reach.
/// </summary>
public sealed partial class BackupStore(IServerFileManager files, ILogger<BackupStore> logger)
{
    /// <summary>Pulsar's own files in a server directory: never backed up, never restored.</summary>
    public const string PulsarDirectory = ".pulsar";

    private const int BufferSize = 256 * 1024;
    private const int SampleSize = 256 * 1024;
    private const int MaxLoggedSkips = 20;
    private static readonly TimeSpan s_progressInterval = TimeSpan.FromMilliseconds(500);

    private static readonly string[] s_alwaysExcluded = [PulsarDirectory + "/**", "**/" + ServerFileRules.TemporaryPrefix + "*"];

    /// <summary>
    /// Archives the files the rules select (everything when there are none) into <paramref name="location"/>. Links are
    /// never followed; files the game holds open are skipped and counted. Nothing is left behind on failure.
    /// </summary>
    public async Task<BackupResult> CreateAsync(BackupLocation location, ProvisionedServer server, BackupDefinition? rules, BackupProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(progress);
        await progress(BackupStages.Scanning, null);
        var selected = Select(files.Walk(server), rules);
        var total = selected.Sum(f => f.Size);
        System.IO.Directory.CreateDirectory(location.Directory);
        try
        {
            int count = 0, skipped = 0;
            await using (var output = new FileStream(location.Partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize,
                FileOptions.Asynchronous))
            {
                await using var archive = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: null,
                    cancellationToken);
                var meter = new ProgressMeter(BackupStages.Archiving, total, progress);
                await meter.ReportAsync(force: true);
                var buffer = new byte[BufferSize];
                foreach (var file in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OpenedFile opened;
                    try
                    {
                        opened = files.OpenRead(server, file.Path, long.MaxValue);
                    }
                    catch (FileManagerException ex)
                    {
                        // Held open by the game, gone or turned into a link since the walk: the rest is still worth keeping.
                        if (++skipped <= MaxLoggedSkips)
                        {
                            LogSkipped(location.ShortId, file.Path, ex.Code);
                        }

                        meter.Add(file.Size);
                        continue;
                    }

                    using (opened)
                    {
                        // The start of the file decides: maps, archives and media barely shrink, and deflate would only add to them.
                        var sampled = await opened.Content.ReadAtLeastAsync(buffer.AsMemory(0, SampleSize), SampleSize, throwOnEndOfStream: false,
                            cancellationToken);
                        var level = Shrinks(buffer.AsSpan(0, sampled)) ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                        var entry = archive.CreateEntry(file.Path, level);
                        entry.LastWriteTime = ZipTime(opened.Info.Entry.ModifiedAt);
                        await using var target = await entry.OpenAsync(cancellationToken);
                        await target.WriteAsync(buffer.AsMemory(0, sampled), cancellationToken);
                        long copied = sampled;
                        meter.Add(sampled);
                        int read;
                        while ((read = await opened.Content.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            copied += read;
                            meter.Add(read);
                            await meter.ReportAsync();
                        }

                        meter.Add(file.Size - copied); // a file that shrank or grew since the walk
                    }

                    count++;
                }

                meter.Complete();
                await meter.ReportAsync(force: true);
            }

            await progress(BackupStages.Checking, null);
            var (size, hash) = await HashAsync(location.Partial, cancellationToken);
            File.Move(location.Partial, location.Archive);
            LogCreated(location.ShortId, location.BackupId, count, skipped, size);
            return new BackupResult(size, count, skipped, hash);
        }
        catch
        {
            TryDelete(location.Partial);
            throw;
        }
    }

    /// <summary>
    /// Writes the backup's files back into the server directory, each replacing the file it had (files the backup does
    /// not have stay). The archive must still hash to <paramref name="sha256"/>, and every entry must be a plain path
    /// inside the server directory outside Pulsar's own files: anything else refuses the whole archive before a byte
    /// is written.
    /// </summary>
    public async Task RestoreAsync(BackupLocation location, ProvisionedServer server, string sha256, BackupProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(progress);
        await progress(BackupStages.Checking, null);
        await using var input = OpenArchive(location, FileOptions.Asynchronous);
        var (_, hash) = await HashAsync(input, cancellationToken);
        if (!string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupException(BackupErrors.Corrupt, "The backup's archive changed since it was made (its SHA-256 differs).");
        }

        input.Position = 0;
        try
        {
            await using var archive = await ZipArchive.CreateAsync(input, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken);
            var entries = Check(archive);
            var meter = new ProgressMeter(BackupStages.Restoring, entries.Sum(e => e.Entry.Length), progress);
            await meter.ReportAsync(force: true);
            var buffer = new byte[BufferSize];
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

                using var pending = files.Create(server, path, entry.Length, overwrite: true, expectedVersion: null);
                await using (var source = await entry.OpenAsync(cancellationToken))
                {
                    var remaining = entry.Length;
                    while (remaining > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0)
                        {
                            throw Corrupt(path, "it is shorter than its declared size");
                        }

                        await pending.Content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        remaining -= read;
                        meter.Add(read);
                        await meter.ReportAsync();
                    }

                    if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0)
                    {
                        throw Corrupt(path, "it is longer than its declared size");
                    }
                }

                pending.Commit();
            }

            meter.Complete();
            await meter.ReportAsync(force: true);
            LogRestored(location.ShortId, location.BackupId, entries.Count);
        }
        catch (InvalidDataException ex)
        {
            throw new BackupException(BackupErrors.Corrupt, $"The backup's archive is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>Opens the archive to send it (the panel streams it to a browser).</summary>
    public OpenedFile Open(BackupLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var stream = OpenArchive(location, FileOptions.Asynchronous);
        var info = new FileInfo(location.Archive);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var entry = new FileEntry(Path.GetFileName(location.Archive), FileEntryKind.File, stream.Length, modified);
        LogOpened(location.ShortId, location.BackupId);
        return new OpenedFile(stream, new FileEntryResult(entry, FileVersion.Of(entry.Size, modified)));
    }

    /// <summary>Deletes the archive; false when there was none.</summary>
    public bool Delete(BackupLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!File.Exists(location.Archive))
        {
            return false;
        }

        try
        {
            File.Delete(location.Archive);
            LogDeleted(location.ShortId, location.BackupId);
            return true;
        }
        catch (IOException ex)
        {
            throw new BackupException(BackupErrors.InUse, "The backup is being downloaded or restored; try again when that ends.", ex);
        }
    }

    /// <summary>Deletes every backup of a server (the server is being deleted).</summary>
    public void DeleteAll(string root, string shortId)
    {
        var directory = new BackupLocation(root, shortId, Guid.Empty).Directory;
        if (System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.Delete(directory, recursive: true);
            LogAllDeleted(shortId);
        }
    }

    /// <summary>Removes the archives of backups the agent was writing when it stopped.</summary>
    public void CleanUp(string root)
    {
        if (!System.IO.Directory.Exists(root))
        {
            return;
        }

        foreach (var partial in System.IO.Directory.EnumerateFiles(root, "*" + BackupLocation.PartialSuffix, SearchOption.AllDirectories))
        {
            TryDelete(partial);
        }
    }

    /// <summary>The files a backup takes: matched by the manifest's globs, minus Pulsar's own; links and directories are not files.</summary>
    internal static List<TreeEntry> Select(IReadOnlyList<TreeEntry> tree, BackupDefinition? rules)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(rules?.Include is { Count: > 0 } include ? include : ["**/*"]);
        matcher.AddExcludePatterns([.. rules?.Exclude ?? [], .. s_alwaysExcluded]);
        var candidates = tree.Where(e => e.Kind == FileEntryKind.File).ToList();
        var root = Path.GetFullPath(Path.DirectorySeparatorChar.ToString());
        var matched = matcher.Match(root, candidates.Select(e => e.Path)).Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        return [.. candidates.Where(e => matched.Contains(e.Path)).OrderBy(e => e.Path, StringComparer.Ordinal)];
    }

    /// <summary>Whether deflate makes <paramref name="sample"/> at least a tenth smaller (small files are always compressed).</summary>
    internal static bool Shrinks(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 4096)
        {
            return true;
        }

        var counter = new CountingStream();
        using (var deflate = new DeflateStream(counter, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(sample);
        }

        return counter.Length < sample.Length * 0.9;
    }

    /// <summary>Every entry, checked before anything is written: a restore may only write plain paths below the server directory.</summary>
    private static List<(ZipArchiveEntry Entry, string Path, bool Directory)> Check(ZipArchive archive)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry, string, bool)>();
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var directory = name.EndsWith('/');
            var path = directory ? name[..^1] : name;
            if (ServerFilePath.Error(path) is { } error)
            {
                throw Corrupt(name, error);
            }

            if (path.Equals(PulsarDirectory, StringComparison.OrdinalIgnoreCase) || ServerFilePath.IsInside(path, PulsarDirectory))
            {
                throw Corrupt(name, "Pulsar's own files are never restored");
            }

            if (!seen.Add(path))
            {
                throw Corrupt(name, "it appears twice");
            }

            entries.Add((entry, path, directory));
        }

        return entries;
    }

    private static FileStream OpenArchive(BackupLocation location, FileOptions options)
    {
        try
        {
            return new FileStream(location.Archive, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, options);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BackupException(BackupErrors.NotFound, "The backup's archive is not on the node anymore.", ex);
        }
    }

    private static async Task<(long Size, string Sha256)> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
        return await HashAsync(stream, cancellationToken);
    }

    private static async Task<(long Size, string Sha256)> HashAsync(Stream stream, CancellationToken cancellationToken) =>
        (stream.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)));

    private static BackupException Corrupt(string entry, string reason) =>
        new(BackupErrors.Corrupt, $"The backup cannot be restored: entry '{entry}': {reason}.");

    /// <summary>Zip stores local times from 1980 to 2107.</summary>
    private static DateTimeOffset ZipTime(DateTimeOffset time)
    {
        var earliest = new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2107, 12, 30, 0, 0, 0, TimeSpan.Zero);
        return time < earliest ? earliest : time > latest ? latest : time;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotDeleted(path, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of {ShortId} made: {Files} files ({Skipped} skipped), {Bytes} bytes")]
    private partial void LogCreated(string shortId, Guid backupId, int files, int skipped, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} restored into {ShortId}: {Entries} entries")]
    private partial void LogRestored(string shortId, Guid backupId, int entries);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Backup {BackupId} of {ShortId} opened to be sent")]
    private partial void LogOpened(string shortId, Guid backupId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of {ShortId} deleted")]
    private partial void LogDeleted(string shortId, Guid backupId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Every backup of {ShortId} deleted")]
    private partial void LogAllDeleted(string shortId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup of {ShortId}: {Path} left out ({Code})")]
    private partial void LogSkipped(string shortId, string path, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Path}: {Reason}")]
    private partial void LogNotDeleted(string path, string reason);

    /// <summary>Counts what is written to it, and keeps nothing.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Progress by bytes, reported at most twice a second.</summary>
    private sealed class ProgressMeter(string stage, long total, BackupProgress progress)
    {
        private readonly Stopwatch _since = Stopwatch.StartNew();
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
