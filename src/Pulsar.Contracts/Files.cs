using System.Text.RegularExpressions;

namespace Pulsar.Contracts;

/// <summary>
/// Paths of the file manager (docs/architecture/08, "File manager"): relative to the server directory, with forward
/// slashes, naming things by their plain names. Checked by the panel and again by the agent; anything Windows would
/// read as a root, a drive, a device, a stream or a parent directory is refused, and so are names Windows would
/// silently change (trailing dots and spaces).
/// </summary>
public static partial class ServerFilePath
{
    public const int MaxLength = 1024;
    public const int MaxSegmentLength = 255;

    /// <summary>The server directory itself.</summary>
    public const string Root = "";

    public static bool IsValid(string? path, bool allowRoot = false) => Error(path, allowRoot) is null;

    /// <summary>Why <paramref name="path"/> is refused, or null when it is a plain path inside the server directory.</summary>
    /// <param name="allowRoot">Whether the empty path (the server directory itself) is acceptable.</param>
    public static string? Error(string? path, bool allowRoot = false)
    {
        if (path is null)
        {
            return "A path is required.";
        }

        if (path.Length == 0)
        {
            return allowRoot ? null : "The server directory itself cannot be the target.";
        }

        if (path.Length > MaxLength)
        {
            return $"At most {MaxLength} characters.";
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return "Use forward slashes.";
        }

        if (path.Contains(':', StringComparison.Ordinal))
        {
            return "Drives and streams are not allowed.";
        }

        foreach (var segment in path.Split('/'))
        {
            if (SegmentError(segment) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>The directory holding <paramref name="path"/> (<see cref="Root"/> for a top-level name).</summary>
    public static string Parent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? Root : path[..slash];
    }

    /// <summary>The last segment of <paramref name="path"/>.</summary>
    public static string Name(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOf('/') + 1)..];
    }

    public static string Join(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return directory.Length == 0 ? name : $"{directory}/{name}";
    }

    /// <summary>Whether <paramref name="path"/> lies below <paramref name="directory"/> (case-insensitive, as on Windows).</summary>
    public static bool IsInside(string path, string directory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(directory);
        return directory.Length == 0
            ? path.Length > 0
            : path.Length > directory.Length + 1 && path[directory.Length] == '/'
                && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SegmentError(string segment)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            return "Empty, '.' and '..' segments are not allowed.";
        }

        if (segment.Length > MaxSegmentLength)
        {
            return $"Names have at most {MaxSegmentLength} characters.";
        }

        if (segment[0] == ' ' || segment[^1] is ' ' or '.')
        {
            return "Names cannot start with a space or end with a dot or a space.";
        }

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*')
            {
                return "Contains characters Windows does not allow in names.";
            }

            if (char.IsHighSurrogate(c) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return "Contains invalid text (a broken character).";
            }
        }

        // Windows maps these names to devices whatever their extension ("nul.txt", "CON .cfg").
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        return ReservedName().IsMatch((dot < 0 ? segment : segment[..dot]).TrimEnd(' '))
            ? $"'{segment}' is a reserved device name on Windows."
            : null;
    }

    [GeneratedRegex("^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[0-9¹²³]|LPT[0-9¹²³])\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}

public enum FileEntryKind
{
    File,
    Directory,

    /// <summary>A junction or symbolic link: shown, renamed and deleted (the link itself), never followed.</summary>
    Link,
}

/// <param name="Size">Bytes, for files; 0 otherwise.</param>
public sealed record FileEntry(string Name, FileEntryKind Kind, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// The version of a file's content for the editor: when it was saved and how large it is. Saving with a version that
/// no longer matches fails (<c>files.changed</c>): someone, or the game, changed the file in between.
/// </summary>
public static class FileVersion
{
    public static string Of(long size, DateTimeOffset modifiedAt) =>
        FormattableString.Invariant($"{size:x}-{modifiedAt.UtcTicks:x}");

    /// <summary>
    /// Whether <paramref name="version"/> has the shape <see cref="Of"/> makes: two lowercase hexadecimal numbers of up to
    /// 16 digits. The panel sends a version back as the ETag, so nothing else (a quote, a control character) gets there.
    /// </summary>
    public static bool IsWellFormed(string? version)
    {
        if (version is null)
        {
            return false;
        }

        var dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && IsHex(version.AsSpan(0, dash)) && IsHex(version.AsSpan(dash + 1));
    }

    private static bool IsHex(ReadOnlySpan<char> number)
    {
        if (number.Length is 0 or > 16)
        {
            return false;
        }

        foreach (var c in number)
        {
            if (!(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Limits of the file manager, the same on both sides.</summary>
public static class FileLimits
{
    /// <summary>Largest file the editor opens or saves.</summary>
    public const long MaxEditableBytes = 1024 * 1024;

    /// <summary>Most entries a listing returns; the rest is left out (<see cref="FileListResult.Truncated"/>) or comes in later pages.</summary>
    public const int MaxListEntries = 2000;
}

/// <summary>
/// The order of paged listings (<see cref="ListFilesCommand.Paged"/>): by name ignoring case, as Windows compares names,
/// then by the exact name, so that two names differing only in case (a case-sensitive folder) still have a fixed order and
/// a page's last name says exactly where the next page starts.
/// </summary>
public static class FileNameOrder
{
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    public static int Compare(string? a, string? b)
    {
        var name = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return name != 0 ? name : string.CompareOrdinal(a, b);
    }
}

/// <summary>
/// What SFTP leaves out of a server's files (Phase 17, docs/architecture/09-roadmap.md): Pulsar's own <c>.pulsar</c> folder
/// (also by a first segment shaped like its 8.3 short name, <c>PULSAR~1</c>) and names with invisible format characters
/// (such as U+202E, which turns the rest of a name around): the web shows those marked, an SFTP client cannot. A hidden path
/// is not found; hidden names are left out of listings (format characters counted in <see cref="FileListResult.Skipped"/>).
/// </summary>
public static partial class SftpView
{
    /// <summary>Whether one name holds a format character (Unicode category Cf).</summary>
    public static bool IsHiddenName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var rune in name.EnumerateRunes())
        {
            if (System.Text.Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="path"/> (a <see cref="ServerFilePath"/>) is in Pulsar's folder or has a hidden name on the way.</summary>
    public static bool IsHidden(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return false;
        }

        var segments = path.Split('/');
        return segments[0].Equals(".pulsar", StringComparison.OrdinalIgnoreCase) || PulsarShortName().IsMatch(segments[0])
            || segments.Any(IsHiddenName);
    }

    // What Windows calls ".pulsar" on a volume that keeps 8.3 names: the leading dot is dropped, then ~N. \z, not $.
    [GeneratedRegex(@"^PULSAR~[0-9]+\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PulsarShortName();
}

/// <summary>Error codes of file commands and transfers (the agent's <see cref="CommandAck.ErrorCode"/>).</summary>
public static class FileErrors
{
    public const string InvalidPath = "files.invalid_path";
    public const string NotFound = "files.not_found";
    public const string Exists = "files.exists";
    public const string NotADirectory = "files.not_a_directory";
    public const string IsADirectory = "files.is_a_directory";
    public const string Link = "files.reparse_point";
    public const string OutsideServer = "files.outside_server";
    public const string AccessDenied = "files.access_denied";
    public const string InUse = "files.in_use";
    public const string TooLarge = "files.too_large";
    public const string Changed = "files.changed";
    public const string DiskFull = "files.disk_full";
    public const string TransferFailed = "files.transfer_failed";

    /// <summary>A download asked to start before the beginning of the file (<see cref="SendFileCommand.Offset"/>).</summary>
    public const string InvalidOffset = "files.invalid_offset";
    public const string Failed = "files.failed";

    /// <summary>A zip that cannot be written out as it is: damaged, encrypted, or naming a path it may not (Pulsar 1.0.11).</summary>
    public const string ArchiveInvalid = "files.archive_invalid";
}

/// <summary>Error codes of backup commands and operations (besides <see cref="FileErrors"/> from reading and writing server files).</summary>
public static class BackupErrors
{
    public const string NotFound = "backups.not_found";

    /// <summary>The archive is not what the node made: its SHA-256 changed, or an entry is one a restore may not write.</summary>
    public const string Corrupt = "backups.corrupt";

    /// <summary>The archive is open (a download or a restore is reading it).</summary>
    public const string InUse = "backups.in_use";

    /// <summary>Remote storage refused or failed (credentials, bucket, network); the message says what it answered.</summary>
    public const string RemoteFailed = "backups.remote_failed";
}

/// <summary>Why a backup read a running game's files live instead of from a snapshot (<see cref="BackupCapture.SnapshotError"/>).</summary>
public static class BackupSnapshotErrors
{
    /// <summary>The agent's administrator turned snapshots off.</summary>
    public const string Disabled = "snapshot.disabled";

    /// <summary>The agent may not take snapshots: a development agent running without administrator rights.</summary>
    public const string NotPermitted = "snapshot.not_permitted";

    /// <summary>The volume cannot have snapshots (FAT, exFAT, a network share) or this system has none.</summary>
    public const string Unsupported = "snapshot.unsupported";

    /// <summary>The Volume Shadow Copy service is disabled or not answering.</summary>
    public const string Unavailable = "snapshot.unavailable";

    /// <summary>The volume has no room left for the snapshot's changes (its shadow storage is full).</summary>
    public const string NoSpace = "snapshot.no_space";

    /// <summary>Other snapshots kept being taken (another backup program) for longer than the agent waits.</summary>
    public const string Busy = "snapshot.busy";

    /// <summary>Anything else; the agent's log says what Windows answered.</summary>
    public const string Failed = "snapshot.failed";

    /// <summary>The game started while its files were read: some may have been written meanwhile.</summary>
    public const string GameStarted = "snapshot.game_started";
}

/// <summary>
/// A directory's entries: directories first, then files and links, each by name; for a paged listing
/// (<see cref="ListFilesCommand.Paged"/>) a page of them by <see cref="FileNameOrder"/> alone.
/// </summary>
/// <param name="Truncated">
/// The directory has more entries than a listing holds; for a paged listing, more than the agent reads of one folder
/// (some entries are in no page).
/// </param>
/// <param name="Skipped">
/// Entries the panel left out because it cannot name them (set by the panel, not the agent: whatever an agent says here is
/// replaced). Distinct from <paramref name="Truncated"/>. A paged listing counts them on its first page only.
/// </param>
/// <param name="Next">
/// Paged listings only: more entries follow; the next page is the one after this name (the page's last). Null: the last page.
/// </param>
public sealed record FileListResult(IReadOnlyList<FileEntry> Entries, bool Truncated, int Skipped = 0, string? Next = null) : CommandResult;

/// <summary>A file about to be sent (<see cref="SendFileCommand"/>), or one just written.</summary>
public sealed record FileEntryResult(FileEntry Entry, string Version) : CommandResult;

/// <summary>
/// How a transfer the agent received ended (<see cref="ReceiveFileCommand"/>): the file is in place, or nothing changed
/// and <paramref name="ErrorCode"/> says why.
/// </summary>
public sealed record TransferResult(string Ticket, string? ErrorCode, string? Message, FileEntryResult? File);

/// <summary>Single-use transfer tickets: 256 random bits, base64url (43 characters).</summary>
public static class TransferTicket
{
    public static string New() => System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static bool IsWellFormed(string? ticket) =>
        ticket is { Length: 43 } && ticket.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
