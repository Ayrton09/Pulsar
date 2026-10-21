using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Files;

/// <summary>
/// The file manager's access to a server directory, on behalf of panel users (docs/architecture/08, "File manager").
/// Paths are <see cref="ServerFilePath"/> paths, checked again here. Implementations never let a junction or link
/// take an operation outside the server directory, never follow a link the user points at (it is renamed or deleted
/// itself), and on Windows act as the server's own account, so the operating system also refuses anything that
/// account could not do. Failures are <see cref="FileManagerException"/> with a <see cref="FileErrors"/> code.
/// </summary>
public interface IServerFileManager
{
    /// <summary>A directory's entries (<see cref="ServerFilePath.Root"/>: the server directory).</summary>
    FileListResult List(ProvisionedServer server, string path);

    /// <summary>
    /// A page of a directory's entries as SFTP lists them (<see cref="ListFilesCommand.Paged"/>, built by
    /// <see cref="ServerFileRules.Page"/>): those after <paramref name="after"/> by <see cref="FileNameOrder"/>.
    /// </summary>
    FileListResult ListPage(ProvisionedServer server, string path, string? after);

    /// <summary>What an entry is (a link is described, not followed); fails when it does not exist.</summary>
    FileEntryResult Stat(ProvisionedServer server, string path);

    /// <summary>
    /// Sets the last write time of a file or directory (refused for links), clamped to what the file system holds; returns
    /// the entry afterwards.
    /// </summary>
    FileEntryResult SetModified(ProvisionedServer server, string path, DateTimeOffset modifiedAt);

    /// <summary>Creates a directory and its missing parents; fails when the directory exists.</summary>
    void CreateDirectory(ProvisionedServer server, string path);

    /// <summary>Creates a directory and its missing parents; one that exists is kept as it is.</summary>
    void EnsureDirectory(ProvisionedServer server, string path);

    /// <summary>
    /// Every entry below the server directory, each directory before what it holds. Links are reported and never
    /// entered; entries whose names no <see cref="ServerFilePath"/> can name (a device name a game created) are left out
    /// and reported in <see cref="TreeWalk.Refused"/>.
    /// </summary>
    TreeWalk Walk(ProvisionedServer server);

    /// <summary>
    /// Renames or moves an entry; fails when <paramref name="destination"/> exists, unless <paramref name="replace"/> and
    /// it is a file (see <see cref="MoveFileCommand.Replace"/>), which is then replaced in one step.
    /// </summary>
    void Move(ProvisionedServer server, string path, string destination, bool replace = false);

    /// <summary>Deletes a file, a link (never its target) or a directory with everything in it.</summary>
    void Delete(ProvisionedServer server, string path);

    /// <summary>Opens a file to send it; fails for directories, links and files larger than <paramref name="maxSize"/>.</summary>
    OpenedFile OpenRead(ProvisionedServer server, string path, long maxSize);

    /// <summary>
    /// Prepares to write <paramref name="size"/> bytes to <paramref name="path"/>: checks the target (see
    /// <see cref="ReceiveFileCommand"/>) and creates a temporary file next to it, which
    /// <see cref="PendingFile.Commit"/> puts in place. Its directory must exist.
    /// </summary>
    /// <param name="sizeIsLimit">
    /// <paramref name="size"/> is the most that will be written, not the exact length (<see cref="ReceiveFileCommand.Streamed"/>):
    /// no space is reserved and the commit takes whatever was written.
    /// </param>
    PendingFile Create(ProvisionedServer server, string path, long size, bool overwrite, string? expectedVersion, bool sizeIsLimit = false);

    /// <summary>
    /// Opens a file to write at its end, in place (<see cref="ReceiveFileCommand.Append"/>), creating it when missing; its
    /// directory must exist. Refused for directories and links, and when <paramref name="expectedVersion"/> is given and
    /// the file is not that version. What is written stays whether or not it is committed; the commit only describes the file.
    /// </summary>
    PendingFile Append(ProvisionedServer server, string path, string? expectedVersion);
}

/// <summary>An entry found by <see cref="IServerFileManager.Walk"/>.</summary>
/// <param name="Path">From the server directory (a <see cref="ServerFilePath"/>).</param>
public sealed record TreeEntry(string Path, FileEntryKind Kind, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// What <see cref="IServerFileManager.Walk"/> found: the entries, and how many it left out because no
/// <see cref="ServerFilePath"/> can name them (a trailing dot, a device name or a control character, created through
/// extended paths), or because they lie beyond the walk's bounds (<see cref="ServerFileRules.MaxWalkDepth"/>,
/// <see cref="ServerFileRules.MaxWalkEntries"/>). A refused directory counts once; what it holds is not looked at.
/// </summary>
public sealed class TreeWalk : IReadOnlyList<TreeEntry>
{
    /// <summary>Most refused names kept to show.</summary>
    public const int MaxRefusedSample = 20;

    private readonly List<TreeEntry> _entries = [];
    private readonly List<string> _sample = [];

    public int Count => _entries.Count;

    /// <summary>The walk holds <see cref="ServerFileRules.MaxWalkEntries"/>: what it meets next is refused.</summary>
    public bool IsFull => _entries.Count >= ServerFileRules.MaxWalkEntries;

    /// <summary>How many entries were left out (at least: past <see cref="IsFull"/>, a folder not read counts once).</summary>
    public int Refused { get; private set; }

    /// <summary>The paths of the first refused entries; may hold characters a log must not print as they are.</summary>
    public IReadOnlyList<string> RefusedSample => _sample;

    public TreeEntry this[int index] => _entries[index];

    public void Add(TreeEntry entry) => _entries.Add(entry);

    public void Refuse(string path)
    {
        Refused++;
        if (_sample.Count < MaxRefusedSample)
        {
            _sample.Add(path);
        }
    }

    public IEnumerator<TreeEntry> GetEnumerator() => _entries.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class FileManagerException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>One of <see cref="FileErrors"/>.</summary>
    public string Code { get; } = code;
}

/// <summary>A file opened for reading, with what it was when opened.</summary>
public sealed class OpenedFile(Stream content, FileEntryResult info) : IDisposable
{
    public Stream Content { get; } = content;

    public FileEntryResult Info { get; } = info;

    public void Dispose() => Content.Dispose();
}

/// <summary>
/// A file being received: written to <see cref="Content"/> (a temporary file next to the target), then put in place
/// by <see cref="Commit"/>. Disposing it without committing removes the temporary file.
/// </summary>
public abstract class PendingFile : IDisposable
{
    public abstract Stream Content { get; }

    /// <summary>
    /// Checks every byte announced was written and the target is still as expected, then replaces it (or creates it)
    /// in one step: readers see the old file or the new one, never half of it.
    /// </summary>
    public abstract FileEntryResult Commit();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);
}

/// <summary>Rules every implementation shares.</summary>
public static class ServerFileRules
{
    /// <summary>Temporary files of transfers in progress (left behind only if the agent stops in the middle of one).</summary>
    public const string TemporaryPrefix = ".pulsar-upload-";

    /// <summary>
    /// The most entries a walk returns (and the deepest folder it goes into): what lies beyond is left out and counted
    /// (<see cref="TreeWalk.Refused"/>), so a game nesting folders without end makes its backups skip them, not fail.
    /// </summary>
    public const int MaxWalkEntries = 1_000_000;

    public const int MaxWalkDepth = 256;

    /// <summary>
    /// The most entries of one folder a listing reads: a folder with more is listed from the first ones the file system
    /// returns, and the listing is marked truncated.
    /// </summary>
    public const int MaxListScan = 1_000_000;

    /// <summary>
    /// The most entries of one folder a page of a paged listing reads (SFTP): every page reads the folder again, so a
    /// client paging through a huge folder costs pages times this. A folder with more is paged from the first ones the
    /// file system returns, and each page is marked truncated.
    /// </summary>
    public const int MaxPageScan = 100_000;

    /// <summary>
    /// Where SteamCMD finds the Steam login the agent lends it during an install (docs/architecture/06, "Steam
    /// accounts"): no file manager operation reaches into it, whatever name or link leads there; a walk does not enter it.
    /// </summary>
    public const string SteamLoginFolder = ".pulsar/steamcmd/config";

    // A listing travels in one hub message: names are counted as if every character were escaped.
    private const int ListingBudget = (int)(AgentProtocol.MaximumMessageSize * 3 / 4);

    public static void Validate(string path, bool allowRoot = false)
    {
        if (ServerFilePath.Error(path, allowRoot) is { } error)
        {
            throw new FileManagerException(FileErrors.InvalidPath, $"'{path}': {error}");
        }
    }

    /// <summary>Whether <paramref name="path"/> (a <see cref="ServerFilePath"/>) is <see cref="SteamLoginFolder"/> or inside it, whatever its case.</summary>
    public static bool IsSteamLoginFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Equals(SteamLoginFolder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(SteamLoginFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static FileManagerException SteamLoginRefused(string path) => new(FileErrors.AccessDenied, $"'{path}': Pulsar keeps this folder to itself.");

    public static string TemporaryName() => TemporaryPrefix + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) + ".tmp";

    /// <summary>
    /// Directories first, then the rest, each by name; cut where the listing would grow too large. Reads
    /// <paramref name="entries"/> once, holding no more than the entries it keeps (a folder may hold millions), and
    /// stops after <see cref="MaxListScan"/>. Names no <see cref="ServerFilePath"/> accepts (a game can create them through
    /// extended paths: a trailing dot, a device name, a lone surrogate that JSON would turn into another name) are left out
    /// before the cap, so they cannot fill it, and counted in <see cref="FileListResult.Skipped"/>.
    /// </summary>
    public static FileListResult Listing(IEnumerable<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // The first entries in order so far, the last of them on top.
        var first = new PriorityQueue<FileEntry, FileEntry>(Comparer<FileEntry>.Create((a, b) => ListingOrder(b, a)));
        var read = 0;
        var skipped = 0;
        foreach (var entry in entries)
        {
            if (++read > MaxListScan)
            {
                break;
            }

            if (!ServerFilePath.IsValid(entry.Name))
            {
                skipped++;
                continue;
            }

            if (first.Count < FileLimits.MaxListEntries)
            {
                first.Enqueue(entry, entry);
            }
            else if (ListingOrder(entry, first.Peek()) < 0)
            {
                first.DequeueEnqueue(entry, entry);
            }
        }

        var sorted = first.UnorderedItems.Select(i => i.Element).Order(Comparer<FileEntry>.Create(ListingOrder)).ToList();
        var kept = Fitting(sorted);
        return new FileListResult(kept == sorted.Count ? sorted : sorted[..kept], kept < read - skipped, skipped);
    }

    /// <summary>
    /// A page of a paged listing (<see cref="ListFilesCommand.Paged"/>): the entries of the folder at <paramref name="path"/>
    /// that come after <paramref name="after"/> by <see cref="FileNameOrder"/>, the first <see cref="FileLimits.MaxListEntries"/>
    /// of them (fewer when the message would grow too large), and the name the next page starts after when more follow.
    /// Reads <paramref name="entries"/> once, holding no more than the entries it keeps, and at most <see cref="MaxPageScan"/>
    /// of them (then <see cref="FileListResult.Truncated"/>): each page costs one read of the folder, never more memory.
    /// Names no <see cref="ServerFilePath"/> accepts and names SFTP hides (<see cref="SftpView"/>) are left out before the cap;
    /// those with format characters and the unusable ones are counted on the first page, Pulsar's own folder is not counted.
    /// </summary>
    public static FileListResult Page(IEnumerable<FileEntry> entries, string path, string? after)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(path);

        // The first entries after the cursor so far, the last of them on top.
        var first = new PriorityQueue<FileEntry, string>(Comparer<string>.Create((a, b) => FileNameOrder.Compare(b, a)));
        var read = 0;
        var skipped = 0;
        var more = false;
        var truncated = false;
        foreach (var entry in entries)
        {
            if (++read > MaxPageScan)
            {
                truncated = true;
                break;
            }

            if (!ServerFilePath.IsValid(entry.Name) || SftpView.IsHiddenName(entry.Name))
            {
                skipped++;
                continue;
            }

            if (SftpView.IsHidden(ServerFilePath.Join(path, entry.Name))
                || (after is not null && FileNameOrder.Compare(entry.Name, after) <= 0))
            {
                continue;
            }

            if (first.Count < FileLimits.MaxListEntries)
            {
                first.Enqueue(entry, entry.Name);
            }
            else
            {
                more = true;
                if (FileNameOrder.Compare(entry.Name, first.Peek().Name) < 0)
                {
                    first.DequeueEnqueue(entry, entry.Name);
                }
            }
        }

        var sorted = first.UnorderedItems.Select(i => i.Element).OrderBy(e => e.Name, FileNameOrder.Comparer).ToList();
        var kept = Fitting(sorted);
        more |= kept < sorted.Count;
        var page = kept == sorted.Count ? sorted : sorted[..kept];
        return new FileListResult(page, truncated, after is null ? skipped : 0, more && kept > 0 ? page[^1].Name : null);
    }

    /// <summary>How many of <paramref name="entries"/>, from the first, fit in one hub message.</summary>
    private static int Fitting(List<FileEntry> entries)
    {
        var budget = ListingBudget;
        var kept = 0;
        while (kept < entries.Count)
        {
            budget -= (entries[kept].Name.Length * 6) + 128;
            if (budget < 0)
            {
                break;
            }

            kept++;
        }

        return kept;
    }

    /// <summary>
    /// The target of a rename that may replace (<see cref="MoveFileCommand.Replace"/>): a file may be replaced, by another
    /// file only; a directory or link is never replaced.
    /// </summary>
    public static void CheckReplace(string destination, FileEntryKind source, FileEntryKind? existing)
    {
        switch (existing)
        {
            case null:
                return;
            case FileEntryKind.Directory:
                throw new FileManagerException(FileErrors.IsADirectory, $"'{destination}' is a directory; it is not replaced.");
            case FileEntryKind.Link:
                throw new FileManagerException(FileErrors.Link, $"'{destination}' is a junction or link; it is not replaced.");
            case FileEntryKind.File when source != FileEntryKind.File:
                throw new FileManagerException(FileErrors.Exists, $"'{destination}' already exists; only a file replaces a file.");
            default:
                return;
        }
    }

    /// <summary>
    /// <paramref name="time"/> as a FILETIME a file system keeps and a <see cref="DateTime"/> reads back: from just after
    /// 1601 (0 would mean "unchanged" to Windows) to the end of year 9999.
    /// </summary>
    public static long FileTime(DateTimeOffset time)
    {
        var ticks = time.UtcTicks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        return Math.Clamp(ticks, 1, DateTime.MaxValue.ToFileTimeUtc());
    }

    private static int ListingOrder(FileEntry a, FileEntry b)
    {
        var kind = (a.Kind == FileEntryKind.Directory ? 0 : 1) - (b.Kind == FileEntryKind.Directory ? 0 : 1);
        if (kind != 0)
        {
            return kind;
        }

        var name = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return name != 0 ? name : string.CompareOrdinal(a.Name, b.Name);
    }

    /// <summary>The target of a write may be replaced as asked (see <see cref="ReceiveFileCommand"/>).</summary>
    public static void CheckTarget(string path, FileEntryResult? existing, bool overwrite, string? expectedVersion)
    {
        if (existing is { Entry.Kind: FileEntryKind.Directory })
        {
            throw new FileManagerException(FileErrors.IsADirectory, $"'{path}' is a directory.");
        }

        if (existing is { Entry.Kind: FileEntryKind.Link })
        {
            throw new FileManagerException(FileErrors.Link, $"'{path}' is a junction or link.");
        }

        if (expectedVersion is not null)
        {
            if (existing?.Version != expectedVersion)
            {
                throw new FileManagerException(FileErrors.Changed, $"'{path}' changed since it was opened.");
            }
        }
        else if (existing is not null && !overwrite)
        {
            throw new FileManagerException(FileErrors.Exists, $"'{path}' already exists.");
        }
    }

    /// <summary>The file manager error for an exception of the .NET file APIs; null for anything else.</summary>
    public static FileManagerException? Translate(string path, Exception ex) => ex switch
    {
        FileManagerException => null,
        FileNotFoundException or DirectoryNotFoundException => new FileManagerException(FileErrors.NotFound, $"'{path}' does not exist.", ex),
        UnauthorizedAccessException => new FileManagerException(FileErrors.AccessDenied, $"'{path}': access denied.", ex),
        IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } =>
            new FileManagerException(FileErrors.InUse, $"'{path}' is in use.", ex),
        IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) } =>
            new FileManagerException(FileErrors.DiskFull, "Not enough disk space.", ex),
        IOException { HResult: unchecked((int)0x800700B7) or unchecked((int)0x80070050) } =>
            new FileManagerException(FileErrors.Exists, $"'{path}' already exists.", ex),
        IOException => new FileManagerException(FileErrors.Failed, $"'{path}': {ex.Message}", ex),
        _ => null,
    };

    /// <summary>
    /// Whether a walk keeps <paramref name="entry"/> (<paramref name="depth"/> folders below the server directory): false
    /// once it is full, and for a folder deeper than <see cref="MaxWalkDepth"/>, which is then refused with what it holds.
    /// </summary>
    public static bool WalkKeeps(TreeWalk walk, FileEntryKind kind, int depth)
    {
        ArgumentNullException.ThrowIfNull(walk);
        return !walk.IsFull && (kind != FileEntryKind.Directory || depth <= MaxWalkDepth);
    }

    public static void CheckMove(string path, string destination)
    {
        Validate(path);
        Validate(destination);
        if (ServerFilePath.IsInside(destination, path))
        {
            throw new FileManagerException(FileErrors.InvalidPath, $"'{path}' cannot be moved inside itself.");
        }
    }
}
