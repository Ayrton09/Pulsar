using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pulsar.Contracts;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Nodes;

/// <summary>
/// Text and lists an agent chooses, bounded where they enter the panel (the hub, and the answers to its commands):
/// browsers, the audit log, the panel's log and notifications read them, and a node is not trusted with what they
/// show (docs/architecture/08, T32). Error codes keep the protocol's shape or become <see cref="InvalidErrorCode"/>;
/// texts lose control, format and line-separator characters (which would forge a log line or hide what they say)
/// and are cut; lists are cut.
/// </summary>
public static partial class AgentInput
{
    /// <summary>What an error code that is not of the protocol's shape becomes.</summary>
    public const string InvalidErrorCode = "agent.invalid_error_code";

    public const int MaxErrorCodeLength = 64;
    public const int MaxMessageLength = 1024;
    public const int MaxStageLength = 64;
    public const int MaxDisks = 16;
    public const int MaxDiskRootLength = 64;
    public const int MaxDiskUses = 4;
    private const int MaxDiskUseLength = 16;
    public const int MaxHostnameLength = 255;
    public const int MaxOsDescriptionLength = 255;
    public const int MaxOsBuildLength = 64;
    public const int MaxCpuModelLength = 255;
    public const int MaxServersRootLength = 1024;
    public const int MaxAgentVersionLength = 32;

    /// <summary>The most entries a listing says it left out: a number to show, as large as the agent scans a folder.</summary>
    public const int MaxSkipped = 1_000_000;

    /// <summary><paramref name="code"/> when it is a protocol error code (lowercase letters, digits, dots, underscores), otherwise <see cref="InvalidErrorCode"/>.</summary>
    public static string? ErrorCode(string? code) => code is null ? null : ErrorCodeShape().IsMatch(code) ? code : InvalidErrorCode;

    /// <summary>
    /// <paramref name="value"/> without the characters that break a line or hide in it, at most <paramref name="maxLength"/>
    /// long (never half of a surrogate pair). Read once and cut as it goes: a message of a megabyte costs no more than its
    /// first <paramref name="maxLength"/> characters do.
    /// </summary>
    public static string Text(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxLength && IsPlain(value))
        {
            return value;
        }

        var text = new StringBuilder(Math.Min(value.Length, maxLength));
        Span<char> utf16 = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            if (!IsVisible(rune))
            {
                continue;
            }

            if (text.Length + rune.Utf16SequenceLength > maxLength)
            {
                break;
            }

            text.Append(utf16[..rune.EncodeToUtf16(utf16)]);
        }

        return text.ToString();
    }

    public static CommandAck Ack(CommandAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return ack with
        {
            ErrorCode = ErrorCode(ack.ErrorCode),
            Message = ack.Message is null ? null : Text(ack.Message, MaxMessageLength),
            Result = Result(ack.Result),
        };
    }

    /// <summary>
    /// What a command answers. A listing keeps the entries the panel can address and says it is incomplete when it
    /// left some out; a file that is not one the panel can show (<see cref="IsFileName"/>, <see cref="FileVersion.IsWellFormed"/>)
    /// becomes no answer at all, which the panel reads as a rejected command.
    /// </summary>
    public static CommandResult? Result(CommandResult? result) => result switch
    {
        FileListResult listing => FileListing(listing),
        FileEntryResult file => IsFile(file) ? file : null,
        AddonsResult addons => Addons(addons),
        _ => result,
    };

    public static OperationProgress Progress(OperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return progress with
        {
            Stage = Text(progress.Stage, MaxStageLength),
            Message = progress.Message is null ? null : Text(progress.Message, MaxMessageLength),
        };
    }

    public static OperationCompleted Completed(OperationCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return completed with
        {
            ErrorCode = ErrorCode(completed.ErrorCode),
            Message = Message(completed.Message),
            Result = completed.Result is BackupResult backup ? backup with { RemoteError = Message(backup.RemoteError) } : Result(completed.Result),
        };
    }

    /// <summary>
    /// The end of a file transfer: its message becomes the detail of the browser's error. A file the panel cannot show
    /// (a name that is not one segment of a path, a version that is not safe as an ETag) turns a success into a failure:
    /// the node did write something, but the panel has nothing true to tell about it.
    /// </summary>
    public static TransferResult Transfer(TransferResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var cleaned = result with { ErrorCode = ErrorCode(result.ErrorCode), Message = Message(result.Message) };
        if (cleaned.File is not { } file || IsFile(file))
        {
            return cleaned;
        }

        return cleaned.ErrorCode is null
            ? cleaned with { ErrorCode = FileErrors.Failed, Message = "The node reported a file the panel cannot show.", File = null }
            : cleaned with { File = null };
    }

    /// <summary>A server's new state: its detail goes into notifications.</summary>
    public static ServerStateChanged StateChanged(ServerStateChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change with { Detail = Message(change.Detail) };
    }

    /// <summary>What an agent says about its machine (enrollment and every Hello), cut to what the node's columns hold.</summary>
    public static NodeInventorySnapshot Inventory(NodeInventory inventory, string? agentVersion, int protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new NodeInventorySnapshot(
            Text(inventory.Hostname, MaxHostnameLength),
            Text(inventory.OsDescription, MaxOsDescriptionLength),
            Text(inventory.OsBuild, MaxOsBuildLength),
            inventory.CpuModel is null ? null : Text(inventory.CpuModel, MaxCpuModelLength),
            inventory.LogicalCores,
            inventory.MemoryTotalBytes,
            inventory.ServersRoot is null ? null : Text(inventory.ServersRoot, MaxServersRootLength),
            Text(agentVersion, MaxAgentVersionLength),
            protocolVersion);
    }

    public static NodeMetrics Metrics(NodeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return metrics.Disks is not { } disks
            ? metrics
            : metrics with
            {
                Disks = [.. disks.Where(d => d is not null).Take(MaxDisks).Select(d => d with
                {
                    Root = Text(d.Root, MaxDiskRootLength),
                    Uses = [.. (d.Uses ?? []).Take(MaxDiskUses).Select(u => Text(u, MaxDiskUseLength))],
                })],
            };
    }

    /// <summary>The release of an addon (a GitHub tag), as the panel keeps and shows it.</summary>
    public static string? AddonRelease(string? release) => release is null ? null : Text(release, ServerAddon.MaxReleaseLength);

    /// <summary>
    /// Whether <paramref name="name"/> is one segment of a path the panel accepts (<see cref="ServerFilePath"/>). Format
    /// characters pass, as they do in paths: the browser shows them (visibly) and the user can open the file. A name
    /// that fails could not be opened, renamed or deleted from the panel, so it is left out rather than rewritten.
    /// </summary>
    public static bool IsFileName(string? name) => name is { Length: > 0 } && !name.Contains('/', StringComparison.Ordinal) && ServerFilePath.IsValid(name);

    private static string? Message(string? message) => message is null ? null : Text(message, MaxMessageLength);

    private static AddonsResult Addons(AddonsResult result) => result.Addons is null
        ? result
        : result with { Addons = [.. result.Addons.Where(a => a is not null).Select(a => a with { Release = AddonRelease(a.Release) })] };

    /// <summary>
    /// A listing of entries that are all usable, at most <see cref="FileLimits.MaxListEntries"/> (<c>Truncated</c> when
    /// there were more), with the number of entries left out for their names or values (<c>Skipped</c>): the agent's own
    /// count of the names it refuses (it drops them before its cap) plus what the panel still refuses, as a number to show.
    /// A page's <c>Next</c> is a name the panel accepts or nothing (then the listing says it is incomplete); a page cut here
    /// goes on after its last entry kept. Whether the page holds what was asked for is checked by the panel's caller,
    /// which knows where the page was to start (<see cref="Servers.ServerFiles.ListPageAsync"/>).
    /// </summary>
    private static FileListResult FileListing(FileListResult listing)
    {
        var read = (listing.Entries ?? []).ToList();
        var usable = read.Where(IsEntry).ToList();
        var cut = usable.Count > FileLimits.MaxListEntries;
        var next = listing.Next is null || IsFileName(listing.Next) ? listing.Next : null;
        var entries = cut ? usable[..FileLimits.MaxListEntries] : usable;
        return listing with
        {
            Entries = entries,
            Truncated = listing.Truncated || (cut && next is null) || (listing.Next is not null && next is null),
            Skipped = (int)Math.Min(MaxSkipped, (long)Math.Clamp(listing.Skipped, 0, MaxSkipped) + read.Count - usable.Count),
            Next = cut && next is not null ? entries[^1].Name : next,
        };
    }

    private static bool IsFile(FileEntryResult file) => IsEntry(file.Entry) && FileVersion.IsWellFormed(file.Version);

    private static bool IsEntry(FileEntry? entry) => entry is not null
        && IsFileName(entry.Name) && Enum.IsDefined(entry.Kind) && entry.Size >= 0;

    /// <summary>Nothing to remove: no control, format or separator character, and no surrogate (checked rune by rune otherwise).</summary>
    private static bool IsPlain(string value)
    {
        foreach (var c in value)
        {
            if (char.IsSurrogate(c) || !IsVisible(new Rune(c)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVisible(Rune rune) => !Rune.IsControl(rune)
        && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    // \z, not $: $ also matches before a final line break.
    [GeneratedRegex(@"^[a-z0-9._]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeShape();
}
