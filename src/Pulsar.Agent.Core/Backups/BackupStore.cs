using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// <summary>The running game was asked to save and the backup waits for it (the manifest's <c>backup.save</c>).</summary>
    public const string Saving = "saving";

    /// <summary>A snapshot of the server's volume is being taken.</summary>
    public const string Snapshotting = "snapshotting";

    /// <summary>A clean restore deletes the files the backup does not have.</summary>
    public const string Cleaning = "cleaning";

    public const string Scanning = "scanning";
    public const string Archiving = "archiving";
    public const string Checking = "checking";
    public const string Restoring = "restoring";
    public const string Uploading = "uploading";
    public const string Downloading = "downloading";
}

/// <summary>Reports a stage and, when known, how far it got (0–100).</summary>
public delegate Task BackupProgress(string stage, double? percent);

public sealed class BackupException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>One of <see cref="BackupErrors"/>, or the <see cref="FileErrors"/> code of a file a clean restore could not delete.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// A restore that first deletes the files the backup's rules select that the backup does not have. The rules are the
/// ones the archive records (those it was made with); <paramref name="Rules"/>, the server's current ones (the
/// manifest's <c>backup</c>), only serve an archive that records none (made before Phase 15).
/// </summary>
public sealed record CleanRestore(BackupDefinition? Rules);

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

    /// <summary>
    /// Beside an archive another node made (Pulsar 1.0.16): every restore of it deletes by the server's own rules, never
    /// by those the archive records, and checks it fits first.
    /// </summary>
    public string ReceivedMarker => Path.Combine(Directory, $"{BackupId:N}.received");
}

/// <summary>
/// Zip backups of server directories, kept on the node (ADR-0012; docs/architecture/08, "Backups and restore"). Server
/// files are read and written only through the <see cref="IServerFileManager"/>: never through a link, and on Windows as
/// the server's account. The archives themselves live in the agent's own directory, out of the servers' reach. Each
/// archive records the rules it was made with in its zip comment (a small JSON object, covered by its SHA-256): a clean
/// restore deletes by those, so rules widened since never reach files the backup could not have held. An archive
/// without a readable record (made before Phase 15) is cleaned by the server's current rules.
/// </summary>
public sealed partial class BackupStore(IServerFileManager files, ILogger<BackupStore> logger, IRemoteBackupStore? remote = null)
{
    /// <summary>Pulsar's own files in a server directory: never backed up, never restored.</summary>
    public const string PulsarDirectory = ".pulsar";

    private const int BufferSize = 256 * 1024;
    private const int SampleSize = 256 * 1024;
    private const int MaxLoggedSkips = 20;
    private static readonly TimeSpan s_progressInterval = TimeSpan.FromMilliseconds(500);

    // The rules an archive records in its zip comment (whose limit is 64 KiB).
    private const int RulesVersion = 1;
    private const int MaxCommentBytes = 16 * 1024;
    private static readonly JsonSerializerOptions s_commentJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 4 };

    private static readonly string[] s_alwaysExcluded = [PulsarDirectory + "/**", "**/" + ServerFileRules.TemporaryPrefix + "*"];

    /// <summary>
    /// Archives the files the rules select (everything when there are none) into <paramref name="location"/>. Links are
    /// never followed; files the game holds open are skipped and counted. Nothing is left behind on failure.
    /// </summary>
    /// <param name="whole">
    /// Every file of the server, whatever its rules, and the records of Pulsar's that go with them (a clone's or a
    /// transfer's copy, 1.0.17): <paramref name="rules"/> play no part.
    /// </param>
    public async Task<BackupResult> CreateAsync(BackupLocation location, ProvisionedServer server, BackupDefinition? rules, BackupProgress progress,
        CancellationToken cancellationToken, bool whole = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(progress);
        await progress(BackupStages.Scanning, null);
        var walk = files.Walk(server);
        List<TreeEntry> selected = whole ? [.. Select(walk, null).Concat(Records(walk)).OrderBy(e => e.Path, StringComparer.Ordinal)] : Select(walk, rules);
        var collisions = DropCaseCollisions(selected);
        var total = selected.Sum(f => f.Size);
        System.IO.Directory.CreateDirectory(location.Directory);
        try
        {
            // A restore refuses an archive naming one path twice by any case (Windows would write both to one file), so a
            // folder with per-directory case sensitivity must not put both in: the first in ordinal order stays. Names the
            // walk could not name (created through extended paths) are not in the backup either: both count as left out.
            int count = 0, skipped = collisions.Count + walk.Refused;
            foreach (var collision in collisions.Take(MaxLoggedSkips))
            {
                LogSkipped(location.ShortId, collision.Path, "case_collision");
            }

            if (walk.Refused > 0)
            {
                LogUnnameable(location.ShortId, walk.Refused, string.Join(", ", walk.RefusedSample.Select(Printable)));
            }

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
                        // At most the size the walk listed: a file read live (no snapshot) that grows as fast as it is read
                        // (a log) would otherwise keep the backup going until the disk is full. What it wrote since is left out.
                        var sample = (int)Math.Min(SampleSize, file.Size);
                        int sampled;
                        try
                        {
                            // The start of the file decides: maps, archives and media barely shrink, and deflate would only add to them.
                            sampled = await opened.Content.ReadAtLeastAsync(buffer.AsMemory(0, sample), sample, throwOnEndOfStream: false,
                                cancellationToken);
                        }
                        catch (IOException ex) when (ServerFileRules.Translate(file.Path, ex) is { Code: FileErrors.InUse })
                        {
                            // Opened, but locked by the game (Minecraft's session.lock, read live): left out, as a file it holds open.
                            if (++skipped <= MaxLoggedSkips)
                            {
                                LogSkipped(location.ShortId, file.Path, FileErrors.InUse);
                            }

                            meter.Add(file.Size);
                            continue;
                        }

                        var level = Shrinks(buffer.AsSpan(0, sampled)) ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                        var entry = archive.CreateEntry(file.Path, level);
                        entry.LastWriteTime = ZipTime(opened.Info.Entry.ModifiedAt);
                        await using var target = await entry.OpenAsync(cancellationToken);
                        await target.WriteAsync(buffer.AsMemory(0, sampled), cancellationToken);
                        long copied = sampled;
                        meter.Add(sampled);
                        int read;
                        try
                        {
                            while (copied < file.Size
                                && (read = await opened.Content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Size - copied)), cancellationToken)) > 0)
                            {
                                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                                copied += read;
                                meter.Add(read);
                                await meter.ReportAsync();
                            }
                        }
                        catch (IOException ex) when (ServerFileRules.Translate(file.Path, ex) is { } failed)
                        {
                            // Locked halfway (its first bytes are in the archive already): the backup fails, saying which file.
                            throw failed;
                        }

                        if (copied == file.Size && await opened.Content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0)
                        {
                            LogCutAtListedSize(location.ShortId, file.Path, file.Size);
                        }

                        meter.Add(file.Size - copied); // a file that shrank since the walk
                    }

                    count++;
                }

                meter.Complete();
                await meter.ReportAsync(force: true);
                if (RulesComment(rules) is { } comment)
                {
                    archive.Comment = comment;
                }
                else
                {
                    LogRulesNotRecorded(location.ShortId, location.BackupId);
                }
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
    /// Writes the backup's files back into the server directory, each replacing the file it had. Files the backup does
    /// not have stay, unless the restore is <paramref name="clean"/>: then the files the backup's rules select that the
    /// backup does not have are deleted first. The archive must still hash to <paramref name="sha256"/>, and every entry
    /// must be a plain path inside the server directory outside Pulsar's own files: anything else refuses the whole
    /// archive before a file is deleted or written. A file the clean part cannot delete (held open, say) is left and
    /// the backup's files are still written; the restore then fails, saying so. So does one whose first folder is named
    /// like an 8.3 short name that leads to another folder of the server (<c>PULSAR~1</c>, <c>.pulsar</c>'s): what the
    /// backup has there is left out, the rest is written.
    /// </summary>
    /// <param name="received">
    /// The archive came from another node (a clone, Pulsar 1.0.16): a clean restore deletes by this server's manifest's
    /// rules only, never by those the archive records, and what it holds must fit on the drive before anything is written.
    /// </param>
    public async Task RestoreAsync(BackupLocation location, ProvisionedServer server, string sha256, CleanRestore? clean, BackupProgress progress,
        CancellationToken cancellationToken, bool received = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(progress);
        received |= File.Exists(location.ReceivedMarker);
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
            // Its list of entries bounded before .NET reads it: an archive can come from another node (a clone, a transfer).
            await FileArchives.CheckDirectoryAsync(input, cancellationToken);
            await using var archive = await ZipArchive.CreateAsync(input, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken);
            var entries = Check(archive);
            if (received)
            {
                long total = 0;
                foreach (var (entry, path, directory) in entries)
                {
                    if (!directory && (entry.Length < 0 || entry.Length > long.MaxValue / 2 - total))
                    {
                        throw Corrupt(path, "it says it holds more than any drive");
                    }

                    total += directory ? 0 : entry.Length;
                }

                FileArchives.EnsureSpace(server.Directory, total);
            }

            var elsewhere = ShortNamesElsewhere(server, entries.Select(e => e.Path));
            var leftOut = entries.Where(e => !e.Directory && elsewhere.Contains(FirstSegment(e.Path))).Select(e => e.Path).ToList();
            entries.RemoveAll(e => elsewhere.Contains(FirstSegment(e.Path)));
            foreach (var folder in leftOut.GroupBy(FirstSegment, StringComparer.OrdinalIgnoreCase))
            {
                LogNotRestored(location.ShortId, location.BackupId, folder.Count(), folder.Key);
            }

            var undeleted = new List<(string Path, FileManagerException Error)>();
            if (clean is not null)
            {
                await progress(BackupStages.Cleaning, null);
                var rules = (received ? null : RecordedRules(archive.Comment)) ?? clean.Rules;
                undeleted = DeleteNewer(location, server, rules, entries.Where(e => !e.Directory).Select(e => e.Path), cancellationToken);
            }

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
            if (undeleted is [var (first, error), ..])
            {
                throw new BackupException(error.Code,
                    $"The backup's files were restored, but {undeleted.Count} newer files could not be deleted (first: {first}: {error.Message}).", error);
            }

            if (leftOut is [var notRestored, ..])
            {
                throw new BackupException(FileErrors.Exists,
                    $"The backup's files were restored, except {leftOut.Count} under '{FirstSegment(notRestored)}': in this server's folder that name is the "
                    + "short name of another folder (Pulsar's own .pulsar, say).");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new BackupException(BackupErrors.Corrupt, $"The backup's archive is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Copies a made backup to remote storage. A failed copy does not fail the backup (it is on the node): the result says
    /// why. A cancelled one removes the archive too, as a cancelled backup leaves nothing.
    /// </summary>
    public async Task<BackupResult> CopyToRemoteAsync(BackupLocation location, BackupResult made, RemoteBackupTarget target, BackupProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(made);
        ArgumentNullException.ThrowIfNull(progress);
        try
        {
            await progress(BackupStages.Uploading, 0);
            await Remote.UploadAsync(target, location.Archive, made.Sha256, percent => progress(BackupStages.Uploading, percent), cancellationToken);
            LogUploaded(location.ShortId, location.BackupId, target.Bucket);
            return made with { RemoteCopied = true };
        }
        catch (BackupException ex)
        {
            LogUploadFailed(location.ShortId, location.BackupId, ex.Message);
            return made with { RemoteError = ex.Message };
        }
        catch (OperationCanceledException)
        {
            TryDelete(location.Archive);
            throw;
        }
    }

    /// <summary>
    /// Brings a backup the node no longer has back from remote storage (before a restore), checked against the SHA-256
    /// the node made it with. The archive stays on the node. A copy larger than <paramref name="sizeBytes"/> (the
    /// archive's size when it was made; null: unknown) is refused while it downloads, before it fills the disk.
    /// </summary>
    public async Task FetchAsync(BackupLocation location, RemoteBackupTarget target, string sha256, long? sizeBytes, BackupProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(progress);
        if (File.Exists(location.Archive))
        {
            return;
        }

        System.IO.Directory.CreateDirectory(location.Directory);
        try
        {
            await progress(BackupStages.Downloading, 0);
            await Remote.DownloadAsync(target, location.Partial, sizeBytes, percent => progress(BackupStages.Downloading, percent), cancellationToken);
            if (sizeBytes is { } expected && new FileInfo(location.Partial).Length > expected)
            {
                throw new BackupException(BackupErrors.Corrupt, "The copy in remote storage is larger than the backup the node made.");
            }

            var (_, hash) = await HashAsync(location.Partial, cancellationToken);
            if (!string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupException(BackupErrors.Corrupt, "The copy in remote storage is not the backup the node made (its SHA-256 differs).");
            }

            File.Move(location.Partial, location.Archive);
            LogFetched(location.ShortId, location.BackupId, target.Bucket);
        }
        finally
        {
            TryDelete(location.Partial);
        }
    }

    /// <summary>
    /// Keeps as this backup an archive another node made (Pulsar 1.0.16), which <paramref name="receive"/> writes into the
    /// stream it is given: it must be <paramref name="sizeBytes"/> bytes hashing to <paramref name="sha256"/>, or nothing
    /// is kept. One the node has already is not fetched again.
    /// </summary>
    public async Task ReceiveAsync(BackupLocation location, Func<Stream, CancellationToken, Task> receive, string sha256, long sizeBytes,
        BackupProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(receive);
        ArgumentNullException.ThrowIfNull(progress);
        if (File.Exists(location.Archive))
        {
            return;
        }

        System.IO.Directory.CreateDirectory(location.Directory);
        // Its size is the other node's word: the drive must take it, with room to spare, before a byte is written.
        FileArchives.EnsureSpace(location.Directory, sizeBytes);
        try
        {
            await progress(BackupStages.Downloading, 0);
            await using (var output = new FileStream(location.Partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await receive(output, cancellationToken);
            }

            var (size, hash) = await HashAsync(location.Partial, cancellationToken);
            if (size != sizeBytes || !string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupException(BackupErrors.Corrupt, "The archive that arrived is not the backup the other node made (its size or SHA-256 differs).");
            }

            // Marked before it is the archive: a restore never finds it without the mark.
            File.WriteAllBytes(location.ReceivedMarker, []);
            File.Move(location.Partial, location.Archive);
            LogReceived(location.ShortId, location.BackupId, size);
        }
        finally
        {
            TryDelete(location.Partial);
        }
    }

    /// <summary>Opens the archive to send it, from remote storage when the node no longer has it.</summary>
    public async Task<OpenedFile> OpenAsync(BackupLocation location, RemoteBackupTarget? target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (target is null || File.Exists(location.Archive))
        {
            return Open(location);
        }

        var copy = await Remote.OpenAsync(target, cancellationToken);
        var entry = new FileEntry(Path.GetFileName(location.Archive), FileEntryKind.File, copy.Length, copy.Modified);
        LogOpenedRemote(location.ShortId, location.BackupId);
        return new OpenedFile(copy.Content, new FileEntryResult(entry, FileVersion.Of(entry.Size, copy.Modified)));
    }

    /// <summary>Deletes the copy in remote storage (one already gone is fine).</summary>
    public Task DeleteRemoteAsync(RemoteBackupTarget target, CancellationToken cancellationToken) => Remote.DeleteAsync(target, cancellationToken);

    private IRemoteBackupStore Remote => remote ?? throw new BackupException(BackupErrors.RemoteFailed, "This agent cannot reach remote storage.");

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
            TryDelete(location.ReceivedMarker);
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

    /// <summary>A name for a log line: control characters, which a game could use to forge lines, are shown as '?'.</summary>
    internal static string Printable(string path) => new([.. path.Select(c => char.IsControl(c) ? '?' : c)]);

    /// <summary>The files a backup takes: matched by the manifest's globs, minus Pulsar's own; links and directories are not files.</summary>
    internal static List<TreeEntry> Select(IReadOnlyList<TreeEntry> tree, BackupDefinition? rules)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(rules?.Include is { Count: > 0 } include ? include : ["**/*"]);
        matcher.AddExcludePatterns([.. rules?.Exclude ?? [], .. s_alwaysExcluded]);
        // A walk names every entry by its long name: a first folder named like a short name (DATA~1) is a folder of that
        // name, never .pulsar's, and is backed up like any other.
        var candidates = tree.Where(e => e.Kind == FileEntryKind.File).ToList();
        var root = Path.GetFullPath(Path.DirectorySeparatorChar.ToString());
        var matched = matcher.Match(root, candidates.Select(e => e.Path)).Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        return [.. candidates.Where(e => matched.Contains(e.Path)).OrderBy(e => e.Path, StringComparer.Ordinal)];
    }

    /// <summary>Removes from <paramref name="selected"/> (in path order) every file whose path equals an earlier one ignoring case, and returns them.</summary>
    private static List<TreeEntry> DropCaseCollisions(List<TreeEntry> selected)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<TreeEntry>();
        selected.RemoveAll(file =>
        {
            if (seen.Add(file.Path))
            {
                return false;
            }

            dropped.Add(file);
            return true;
        });
        return dropped;
    }

    /// <summary>
    /// The clean part of a restore: deletes the files the rules select that the backup does not have (made after it).
    /// What the rules leave out (the installed game, logs, Pulsar's own files) and links are never touched. A file that
    /// cannot be deleted does not stop the others; those are returned.
    /// </summary>
    private List<(string Path, FileManagerException Error)> DeleteNewer(BackupLocation location, ProvisionedServer server, BackupDefinition? rules,
        IEnumerable<string> kept, CancellationToken cancellationToken)
    {
        var keep = kept.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        var failed = new List<(string, FileManagerException)>();
        var walk = files.Walk(server);
        foreach (var file in Select(walk, rules))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keep.Contains(file.Path))
            {
                continue;
            }

            try
            {
                files.Delete(server, file.Path);
                deleted++;
            }
            catch (FileManagerException ex) when (ex.Code == FileErrors.NotFound)
            {
                // Gone meanwhile.
            }
            catch (FileManagerException ex)
            {
                // Held open, or refused: the backup's files are still worth writing back.
                if (failed.Count < MaxLoggedSkips)
                {
                    LogNotCleaned(location.ShortId, file.Path, ex.Code);
                }

                failed.Add((file.Path, ex));
            }
        }

        // What the walk left out (deeper than it goes, past its size, a name no path can say) under what the rules select
        // was never looked at: the restore says so rather than call itself clean.
        foreach (var refused in Select([.. walk.RefusedSample.Select(p => new TreeEntry(p, FileEntryKind.File, 0, DateTimeOffset.MinValue))], rules))
        {
            failed.Add((refused.Path, new FileManagerException(FileErrors.Failed,
                "not looked at: it is deeper than 256 folders, past a million entries, or named so no path can say it")));
        }

        LogCleaned(location.ShortId, location.BackupId, deleted);
        return failed;
    }

    /// <summary>The rules a backup is made with, as its archive records them; null when too large to record.</summary>
    private static string? RulesComment(BackupDefinition? rules)
    {
        var comment = JsonSerializer.Serialize(new RecordedRulesComment(RulesVersion, rules?.Include, rules?.Exclude), s_commentJson);
        return Encoding.UTF8.GetByteCount(comment) <= MaxCommentBytes ? comment : null;
    }

    /// <summary>The rules an archive records; null when it records none Pulsar can read (made before Phase 15, or not Pulsar's).</summary>
    private static BackupDefinition? RecordedRules(string? comment)
    {
        if (string.IsNullOrEmpty(comment) || Encoding.UTF8.GetByteCount(comment) > MaxCommentBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecordedRulesComment>(comment, s_commentJson) is { Pulsar: RulesVersion } recorded
                && Valid(recorded.Include) && Valid(recorded.Exclude)
                ? new BackupDefinition(recorded.Include, recorded.Exclude)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        static bool Valid(IReadOnlyList<string>? globs) => globs is null || globs.All(g => !string.IsNullOrEmpty(g));
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

            // By any case. A first segment shaped like its 8.3 short name (PULSAR~1) is checked against the server's folder
            // before anything is written (see ShortNamesElsewhere). The records a whole copy carries are the exception.
            if (FirstSegment(path).Equals(PulsarDirectory, StringComparison.OrdinalIgnoreCase) && (directory || !IsCarriedRecord(path)))
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

    private static string FirstSegment(string path) => path.Split('/')[0];

    private static IEnumerable<TreeEntry> Records(IReadOnlyList<TreeEntry> tree) => tree.Where(e => e.Kind == FileEntryKind.File && IsCarriedRecord(e.Path));

    /// <summary>
    /// What the agent recorded of the server's mods, addons, pack and lists, which a whole copy of it (a clone's, a
    /// transfer's, 1.0.17) takes along so the panel's records of them hold on its new copy: nothing else of Pulsar's folder
    /// (never its SteamCMD, whose login it keeps). The server's account can write them, so the agent trusts none of them
    /// anyway. By exact path (<c>.pulsar</c> in lowercase, as the agent writes it).
    /// </summary>
    internal static bool IsCarriedRecord(string path)
    {
        var segments = path.Split('/');
        return segments switch
        {
            [PulsarDirectory, "modpack.json"] => true,
            [PulsarDirectory, "packages" or "addons" or "lists", var name] => name.Length > ".json".Length
                && name.EndsWith(".json", StringComparison.Ordinal) && !name.StartsWith(ServerFileRules.TemporaryPrefix, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// The archive's first folders named like 8.3 short names (<c>DATA~1</c>) that lead to another folder of the server:
    /// on a volume that keeps short names, <c>PULSAR~1</c> is <c>.pulsar</c> itself. A backup names its folders by their
    /// long names, so such a folder is restored only as a folder of that very name, one the server has or the restore
    /// creates now; the file manager refuses to create one whose name already leads elsewhere.
    /// </summary>
    private HashSet<string> ShortNamesElsewhere(ProvisionedServer server, IEnumerable<string> paths)
    {
        var elsewhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shortNamed = paths.Select(FirstSegment).Where(FileCommands.IsShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (shortNamed.Count == 0)
        {
            return elsewhere;
        }

        var named = files.List(server, ServerFilePath.Root).Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Backup {BackupId} of server {ShortId}: {Count} files under {Folder} not restored, since that name leads to another folder of the server")]
    private partial void LogNotRestored(string shortId, Guid backupId, int count, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of server {ShortId} copied to remote storage (bucket {Bucket})")]
    private partial void LogUploaded(string shortId, Guid backupId, string bucket);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup {BackupId} of server {ShortId} was not copied to remote storage: {Reason}")]
    private partial void LogUploadFailed(string shortId, Guid backupId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of server {ShortId} fetched from remote storage (bucket {Bucket})")]
    private partial void LogFetched(string shortId, Guid backupId, string bucket);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of {ShortId} received from the panel ({Bytes} bytes)")]
    private partial void LogReceived(string shortId, Guid backupId, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of server {ShortId} sent from remote storage")]
    private partial void LogOpenedRemote(string shortId, Guid backupId);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup of {ShortId}: {Path} grew while it was read; kept its first {Size} bytes, as listed")]
    private partial void LogCutAtListedSize(string shortId, string path, long size);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of {ShortId} made: {Files} files ({Skipped} skipped), {Bytes} bytes")]
    private partial void LogCreated(string shortId, Guid backupId, int files, int skipped, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} restored into {ShortId}: {Entries} entries")]
    private partial void LogRestored(string shortId, Guid backupId, int entries);

    [LoggerMessage(Level = LogLevel.Information, Message = "Clean restore of backup {BackupId} into {ShortId}: {Files} newer files deleted first")]
    private partial void LogCleaned(string shortId, Guid backupId, int files);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clean restore into {ShortId}: {Path} could not be deleted ({Code})")]
    private partial void LogNotCleaned(string shortId, string path, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup {BackupId} of {ShortId} does not record its rules (too many): a clean restore of it uses the server's rules then")]
    private partial void LogRulesNotRecorded(string shortId, Guid backupId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Backup {BackupId} of {ShortId} opened to be sent")]
    private partial void LogOpened(string shortId, Guid backupId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {BackupId} of {ShortId} deleted")]
    private partial void LogDeleted(string shortId, Guid backupId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Every backup of {ShortId} deleted")]
    private partial void LogAllDeleted(string shortId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup of {ShortId}: {Path} left out ({Code})")]
    private partial void LogSkipped(string shortId, string path, string code);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Backup of {ShortId}: {Count} entries left out because no path can name them (a trailing dot, a device name, a control character) or because they lie deeper than 256 folders or past a million entries, e.g. {Paths}")]
    private partial void LogUnnameable(string shortId, int count, string paths);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Path}: {Reason}")]
    private partial void LogNotDeleted(string path, string reason);

    /// <summary>The archive comment: <c>{"pulsar":1,"include":[…]|null,"exclude":[…]|null}</c>.</summary>
    private sealed record RecordedRulesComment(int Pulsar, IReadOnlyList<string>? Include, IReadOnlyList<string>? Exclude);

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
