using System.Text.Json.Serialization;

namespace Pulsar.Contracts;

/// <summary>
/// What the panel signs for every command (docs/architecture/03). The agent checks, in order: pinned key, signature,
/// recipient node and session, sequence (anti-replay window), expiry, protocol version and command type.
/// </summary>
public sealed record CommandEnvelope(
    int ProtocolVersion,
    Guid CommandId,
    Guid NodeId,
    Guid SessionId,
    long Sequence,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    Guid? OperationId,
    Guid? ActorUserId,
    AgentCommand Command);

/// <summary>
/// Base of every panel → agent command. Discriminators are stable strings (part of the protocol); an agent that
/// does not know one answers <see cref="AckStatus.UnsupportedCommand"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PingCommand), PingCommand.Type)]
[JsonDerivedType(typeof(RotateCredentialCommand), RotateCredentialCommand.Type)]
[JsonDerivedType(typeof(UpdatePanelKeysCommand), UpdatePanelKeysCommand.Type)]
[JsonDerivedType(typeof(ProvisionServerCommand), ProvisionServerCommand.Type)]
[JsonDerivedType(typeof(InstallServerCommand), InstallServerCommand.Type)]
[JsonDerivedType(typeof(StartServerCommand), StartServerCommand.Type)]
[JsonDerivedType(typeof(StopServerCommand), StopServerCommand.Type)]
[JsonDerivedType(typeof(RestartServerCommand), RestartServerCommand.Type)]
[JsonDerivedType(typeof(KillServerCommand), KillServerCommand.Type)]
[JsonDerivedType(typeof(DeleteServerCommand), DeleteServerCommand.Type)]
[JsonDerivedType(typeof(UpdateServerCommand), UpdateServerCommand.Type)]
[JsonDerivedType(typeof(CheckGameUpdateCommand), CheckGameUpdateCommand.Type)]
[JsonDerivedType(typeof(ApplyServerSpecCommand), ApplyServerSpecCommand.Type)]
[JsonDerivedType(typeof(CancelOperationCommand), CancelOperationCommand.Type)]
[JsonDerivedType(typeof(SendConsoleInputCommand), SendConsoleInputCommand.Type)]
[JsonDerivedType(typeof(SubscribeConsoleCommand), SubscribeConsoleCommand.Type)]
[JsonDerivedType(typeof(UnsubscribeConsoleCommand), UnsubscribeConsoleCommand.Type)]
[JsonDerivedType(typeof(ListFilesCommand), ListFilesCommand.Type)]
[JsonDerivedType(typeof(CreateDirectoryCommand), CreateDirectoryCommand.Type)]
[JsonDerivedType(typeof(MoveFileCommand), MoveFileCommand.Type)]
[JsonDerivedType(typeof(DeleteFileCommand), DeleteFileCommand.Type)]
[JsonDerivedType(typeof(SendFileCommand), SendFileCommand.Type)]
[JsonDerivedType(typeof(ReceiveFileCommand), ReceiveFileCommand.Type)]
[JsonDerivedType(typeof(StatFileCommand), StatFileCommand.Type)]
[JsonDerivedType(typeof(SetFileModifiedCommand), SetFileModifiedCommand.Type)]
[JsonDerivedType(typeof(CreateBackupCommand), CreateBackupCommand.Type)]
[JsonDerivedType(typeof(RestoreBackupCommand), RestoreBackupCommand.Type)]
[JsonDerivedType(typeof(DeleteBackupCommand), DeleteBackupCommand.Type)]
[JsonDerivedType(typeof(SendBackupCommand), SendBackupCommand.Type)]
[JsonDerivedType(typeof(InstallAddonCommand), InstallAddonCommand.Type)]
[JsonDerivedType(typeof(RemoveAddonCommand), RemoveAddonCommand.Type)]
[JsonDerivedType(typeof(ForgetSteamLoginCommand), ForgetSteamLoginCommand.Type)]
[JsonDerivedType(typeof(UseAgentsAddressCommand), UseAgentsAddressCommand.Type)]
[JsonDerivedType(typeof(UpdatePulsarCommand), UpdatePulsarCommand.Type)]
[JsonDerivedType(typeof(ListPortsInUseCommand), ListPortsInUseCommand.Type)]
public abstract record AgentCommand;

/// <summary>Round-trip check of the signed command path (the UI's "test connection").</summary>
public sealed record PingCommand(string? Message) : AgentCommand
{
    public const string Type = "node.ping";
}

/// <summary>
/// Asks the agent to replace its node key now (docs/architecture/03, "Rotation"). The ack only means the rotation
/// started; the agent then registers a new key and re-authenticates with it.
/// </summary>
public sealed record RotateCredentialCommand : AgentCommand
{
    public const string Type = "node.rotate_credential";
}

/// <summary>
/// The panel keys the agent must accept from now on. Authenticated like every command: it is signed by a key the
/// agent already pins, so a panel key rotation never needs a new trust decision on the node.
/// </summary>
public sealed record UpdatePanelKeysCommand(IReadOnlyList<PanelKey> Keys) : AgentCommand
{
    public const string Type = "node.update_panel_keys";
}

/// <summary>
/// The panel's agents' address (Phase 16), where the agent presents its node key as a TLS client certificate: the port of
/// the agents' endpoint on the host the agent already reaches the panel by, or <paramref name="Url"/> (an https origin)
/// when the panel's operator configured one. Never a host taken from a request, which a proxy could choose. The agent
/// signs in there first and only then keeps it, with the address it used until now as a fallback; an address it cannot
/// sign in at is refused (<c>agent.address_unreachable</c>) and changes nothing.
/// </summary>
public sealed record UseAgentsAddressCommand(int Port, string? Url = null) : AgentCommand
{
    public const string Type = "node.use_agents_address";
}

/// <summary>
/// Updates Pulsar on the node to <paramref name="Version"/> (Phase 19): the agent, and the panel too where it runs on the
/// same machine. The agent takes nothing else from the panel: it downloads the release from GitHub itself, checks its
/// manifest against the update keys it trusts (<see cref="UpdateKeys"/>) and that it is the manifest whose SHA-256 is
/// <paramref name="ManifestSha256"/> (the one the panel checked and the administrator chose), refuses a version that is
/// not newer than its own, and then runs the release's installer, which backs up, updates, checks and, when the new
/// version does not come up, puts the old one back. Its progress and its end come as <see cref="UpdateReport"/>s.
/// </summary>
/// <param name="Attempt">
/// The panel's id for this request, sent back with every report about it (and through the installer's result), so the
/// panel tells it from an earlier request of the same version. Null from panels older than request ids (agents older
/// than them ignore it).
/// </param>
public sealed record UpdatePulsarCommand(string Version, string ManifestSha256, Guid? Attempt = null) : AgentCommand
{
    public const string Type = "node.update_pulsar";
}

/// <summary>
/// The ports in use on the node's machine, by any program (1.0.7): the TCP ports listened on and the UDP ports bound,
/// answered as a <see cref="PortsInUseResult"/>. The create dialog skips them when it proposes a server's ports, and says
/// so when one is typed, so a game is not given a port another program holds.
/// </summary>
public sealed record ListPortsInUseCommand : AgentCommand
{
    public const string Type = "node.ports_in_use";
}

/// <summary>
/// Operation (the envelope carries its id): creates or repairs the server's directory and Windows account and stores
/// its runtime spec. Completion is reported with <see cref="OperationCompleted"/>.
/// </summary>
public sealed record ProvisionServerCommand(Guid ServerId, ServerRuntimeSpec Spec) : AgentCommand
{
    public const string Type = "server.provision";
}

/// <summary>Operation: runs the manifest's install steps.</summary>
/// <param name="Login">The Steam account SteamCMD logs in with (Phase 13); null: anonymous.</param>
public sealed record InstallServerCommand(Guid ServerId, SteamLogin? Login = null) : AgentCommand
{
    public const string Type = "server.install";
}

public sealed record StartServerCommand(Guid ServerId) : AgentCommand
{
    public const string Type = "server.start";
}

/// <summary>Graceful stop: the manifest's stop steps, then the process tree is killed after their timeout.</summary>
public sealed record StopServerCommand(Guid ServerId) : AgentCommand
{
    public const string Type = "server.stop";
}

/// <summary>Stop and start as one unit: nothing else runs on the server in between.</summary>
public sealed record RestartServerCommand(Guid ServerId) : AgentCommand
{
    public const string Type = "server.restart";
}

/// <summary>Kills the whole process tree now.</summary>
public sealed record KillServerCommand(Guid ServerId) : AgentCommand
{
    public const string Type = "server.kill";
}

/// <summary>Operation: kills the server if needed, then removes its directory, account and state from the node.</summary>
public sealed record DeleteServerCommand(Guid ServerId) : AgentCommand
{
    public const string Type = "server.delete";
}

/// <summary>Operation: runs the manifest's update steps (a game update through SteamCMD).</summary>
/// <param name="Validate">Also verify every installed file against Steam (slow; repairs a damaged install).</param>
/// <param name="Login">The Steam account SteamCMD logs in with (Phase 13); null: anonymous.</param>
public sealed record UpdateServerCommand(Guid ServerId, bool Validate, SteamLogin? Login = null) : AgentCommand
{
    public const string Type = "server.update";
}

/// <summary>
/// Checks whether the game has an update (Pulsar 1.0.9): the agent answers at once and reports what it found with
/// <see cref="IAgentHub.GameUpdateChecked"/>, since asking Steam through SteamCMD takes longer than an ack may.
/// </summary>
public sealed record CheckGameUpdateCommand(Guid ServerId) : AgentCommand
{
    public const string Type = "server.update_check";
}

/// <summary>
/// A new runtime spec for a provisioned server (variables, ports, arguments, restart policy). Takes effect at the next
/// start; the manifest must be the one the server was provisioned with.
/// </summary>
public sealed record ApplyServerSpecCommand(Guid ServerId, ServerRuntimeSpec Spec) : AgentCommand
{
    public const string Type = "server.apply_spec";
}

/// <summary>
/// Operation: installs (or brings to its latest release) one of the manifest's addons (docs/architecture/04, "Addons").
/// Completes with an <see cref="AddonsResult"/>.
/// </summary>
public sealed record InstallAddonCommand(Guid ServerId, string AddonKey) : AgentCommand
{
    public const string Type = "server.addon_install";
}

/// <summary>
/// Operation: removes an addon: the files it added and its lines; game files it replaced are restored by a validating
/// update. Completes with an <see cref="AddonsResult"/> naming it without a release.
/// </summary>
/// <param name="Login">For the validating update, as in <see cref="UpdateServerCommand"/>.</param>
public sealed record RemoveAddonCommand(Guid ServerId, string AddonKey, SteamLogin? Login = null) : AgentCommand
{
    public const string Type = "server.addon_remove";
}

/// <summary>Cancels a running operation (an install or update kills SteamCMD); it then completes as cancelled.</summary>
public sealed record CancelOperationCommand(Guid OperationId) : AgentCommand
{
    public const string Type = "operation.cancel";
}

/// <summary>
/// A command for a running server's console: written to its input (<c>pty</c>, <c>pipes</c>) or sent over RCON
/// (<c>logfile+rcon</c>). The output shows up in the console stream.
/// </summary>
public sealed record SendConsoleInputCommand(Guid ServerId, string Line) : AgentCommand
{
    public const string Type = "server.console_input";
}

/// <summary>
/// Starts sending the server's console output to the panel (<see cref="IAgentHub.ConsoleOutput"/>): first what the
/// agent still holds after <paramref name="FromSequence"/> (everything it holds when null), then new output as it comes.
/// Repeating it moves the subscription back to <paramref name="FromSequence"/> (the panel drops what it already has).
/// </summary>
public sealed record SubscribeConsoleCommand(Guid ServerId, long? FromSequence) : AgentCommand
{
    public const string Type = "server.console_subscribe";
}

/// <summary>Nobody watches the server's console anymore: its output stays on the node.</summary>
public sealed record UnsubscribeConsoleCommand(Guid ServerId) : AgentCommand
{
    public const string Type = "server.console_unsubscribe";
}

/// <summary>Lists a directory of the server (<see cref="ServerFilePath.Root"/>: the server directory); the ack carries a <see cref="FileListResult"/>.</summary>
/// <param name="Paged">
/// SFTP's listing (Phase 17; only for agents that announce <see cref="AgentCapabilities.SftpFiles"/>): entries by name
/// alone (<see cref="FileNameOrder"/>), only those after <paramref name="After"/>, up to <see cref="FileLimits.MaxListEntries"/>,
/// with <see cref="FileListResult.Next"/> to ask for the rest; what SFTP hides (<see cref="SftpView"/>) is left out. False:
/// the web's listing, directories first, cut at <see cref="FileLimits.MaxListEntries"/>.
/// </param>
/// <param name="After">The previous page's <see cref="FileListResult.Next"/> (one name); null for the first page.</param>
public sealed record ListFilesCommand(Guid ServerId, string Path, bool Paged = false, string? After = null) : AgentCommand
{
    public const string Type = "server.files_list";
}

/// <summary>
/// What one entry of the server is, as SFTP sees it (Phase 17; <see cref="AgentCapabilities.SftpFiles"/>): the ack carries
/// a <see cref="FileEntryResult"/>. A link is described, never followed; what SFTP hides (<see cref="SftpView"/>) is not found.
/// </summary>
public sealed record StatFileCommand(Guid ServerId, string Path) : AgentCommand
{
    public const string Type = "server.files_stat";
}

/// <summary>
/// Sets the last write time of a file or directory (never a link), as SFTP clients do to keep a file's time (Phase 17;
/// <see cref="AgentCapabilities.SftpFiles"/>). A time the file system cannot hold is clamped to the nearest one it can; the
/// ack carries the entry's <see cref="FileEntryResult"/> afterwards. What SFTP hides (<see cref="SftpView"/>) is not found.
/// </summary>
public sealed record SetFileModifiedCommand(Guid ServerId, string Path, DateTimeOffset ModifiedAt) : AgentCommand
{
    public const string Type = "server.files_set_modified";
}

/// <summary>Creates a directory and any missing parents.</summary>
public sealed record CreateDirectoryCommand(Guid ServerId, string Path) : AgentCommand
{
    public const string Type = "server.files_create_directory";
}

/// <summary>Renames or moves a file, directory or link inside the server directory.</summary>
/// <param name="Replace">
/// An existing file at <paramref name="Destination"/> is replaced in one step (SFTP's <c>posix-rename@openssh.com</c>;
/// Phase 17, only for agents that announce <see cref="AgentCapabilities.SftpFiles"/>). Only a file is replaced, and only by a
/// file: a directory or link there, or a directory or link moved onto a file, is refused. False: an existing destination
/// always fails (<c>files.exists</c>).
/// </param>
public sealed record MoveFileCommand(Guid ServerId, string Path, string Destination, bool Replace = false) : AgentCommand
{
    public const string Type = "server.files_move";
}

/// <summary>Deletes a file, a link (never its target) or a directory with everything in it.</summary>
public sealed record DeleteFileCommand(Guid ServerId, string Path) : AgentCommand
{
    public const string Type = "server.files_delete";
}

/// <summary>
/// Sends a file to the panel (docs/architecture/03, "Large transfers"): the agent checks it can (the ack carries a
/// <see cref="FileEntryResult"/>), then uploads it with <c>PUT</c> to <see cref="AgentProtocol.TransferPath"/>.
/// </summary>
/// <param name="MaxSize">Larger files are refused (<c>files.too_large</c>): the editor opens small files only.</param>
/// <param name="Offset">
/// Where the content sent starts (resumed downloads, SFTP reads; Phase 17, only for agents that announce
/// <see cref="AgentCapabilities.SftpFiles"/>): the agent sends the rest of the file from there, nothing when it is at or past
/// the end (a read past the end of a file reads nothing). The ack still describes the whole file. A negative offset is
/// refused (<c>files.invalid_offset</c>).
/// </param>
public sealed record SendFileCommand(Guid ServerId, string Path, string Ticket, long MaxSize, long Offset = 0) : AgentCommand
{
    public const string Type = "server.files_send";

    // The ticket is a secret: it must never reach a log through ToString.
    protected override bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"ServerId = {ServerId}, Path = {Path}, Ticket = ***, MaxSize = {MaxSize}, Offset = {Offset}");
        return true;
    }
}

/// <summary>
/// Receives a file from the panel: the agent checks it can, downloads exactly <paramref name="Size"/> bytes with
/// <c>GET</c> from <see cref="AgentProtocol.TransferPath"/> into a temporary file next to the target, puts it in place
/// and reports the end with <see cref="IAgentHub.TransferCompleted"/>.
/// </summary>
/// <param name="Overwrite">Replace an existing file; without it an existing name fails (<c>files.exists</c>).</param>
/// <param name="ExpectedVersion">
/// The <see cref="FileVersion"/> the file must still have (an editor saving what it opened); implies
/// <paramref name="Overwrite"/>.
/// </param>
/// <param name="Streamed">
/// The length is not known in advance (SFTP; Phase 17, only for agents that announce <see cref="AgentCapabilities.SftpFiles"/>):
/// <paramref name="Size"/> is the most that may arrive (more fails the transfer), and the file is what arrived once the panel
/// ends the content cleanly; an interrupted transfer is a failure, never a shorter file. False: exactly <paramref name="Size"/> bytes.
/// </param>
/// <param name="Append">
/// The content is added at the end of the file, which is created when missing (resumed SFTP uploads; Phase 17, with
/// <see cref="AgentCapabilities.SftpFiles"/>). Written in place as it arrives, not through a temporary file: an interrupted
/// transfer leaves what arrived, which is what resuming needs. <paramref name="Overwrite"/> does not apply;
/// <paramref name="ExpectedVersion"/>, when given, is what the existing file must still be.
/// </param>
public sealed record ReceiveFileCommand(Guid ServerId, string Path, string Ticket, long Size, bool Overwrite, string? ExpectedVersion,
    bool Streamed = false, bool Append = false)
    : AgentCommand
{
    public const string Type = "server.files_receive";

    // The ticket is a secret: it must never reach a log through ToString.
    protected override bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"ServerId = {ServerId}, Path = {Path}, Ticket = ***, Size = {Size}, Overwrite = {Overwrite}, ExpectedVersion = {ExpectedVersion}");
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $", Streamed = {Streamed}, Append = {Append}");
        return true;
    }
}

/// <summary>
/// S3-compatible storage a backup is copied to (AWS S3, Cloudflare R2, Backblaze B2, SeaweedFS…). The panel sends it,
/// secret included, with each backup command that needs it; the agent keeps none of it (docs/architecture/08).
/// </summary>
/// <param name="Endpoint">The service's address (https://…); null for AWS S3 in <paramref name="Region"/>.</param>
/// <param name="Key">The backup's object key in the bucket.</param>
/// <param name="PathStyle">The bucket in the path rather than in the host name (most S3-compatible services).</param>
public sealed record RemoteBackupTarget(string? Endpoint, string Region, string Bucket, string Key, string AccessKeyId, string SecretAccessKey,
    bool PathStyle)
{
    // Records print every member: the secret must never reach a log through ToString.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Endpoint = {Endpoint}, Region = {Region}, Bucket = {Bucket}, Key = {Key}, AccessKeyId = {AccessKeyId}, PathStyle = {PathStyle}");
        return true;
    }
}

/// <summary>
/// A Steam account for SteamCMD (Phase 13), sent with each install and update that needs it and never stored by the
/// agent; only the login SteamCMD caches is kept, encrypted, per <paramref name="AccountId"/>.
/// </summary>
/// <param name="AccountId">The panel's id for the account: the agent keeps its cached login under it.</param>
/// <param name="GuardCode">A Steam Guard code (from the mail or the mobile app) when Steam asked for one.</param>
public sealed record SteamLogin(Guid AccountId, string UserName, string Password, string? GuardCode = null)
{
    // Records print every member: the password and the code must never reach a log through ToString.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"AccountId = {AccountId}, UserName = {UserName}, Password = ***, GuardCode = {(GuardCode is null ? "null" : "***")}");
        return true;
    }
}

/// <summary>
/// The Steam account <paramref name="AccountId"/> was deleted in the panel: the agent deletes the login SteamCMD cached
/// for it, with which the account's user name alone would sign in. Nodes that miss it get it when they connect again:
/// their Hello names the logins they cached (<see cref="AgentHello.CachedSteamLogins"/>).
/// </summary>
public sealed record ForgetSteamLoginCommand(Guid AccountId) : AgentCommand
{
    public const string Type = "steam.forget_login";
}

/// <summary>
/// Operation: archives the server's files (the manifest's <c>backup</c> globs) into a zip kept on the node
/// (docs/architecture/03, "Backups"), then copies it to <paramref name="Remote"/> when there is one. The server may keep
/// running. The result carries a <see cref="BackupResult"/>.
/// </summary>
public sealed record CreateBackupCommand(Guid ServerId, Guid BackupId, RemoteBackupTarget? Remote = null) : AgentCommand
{
    public const string Type = "server.backup_create";
}

/// <summary>
/// Operation: puts the backup's files back in the server directory. The server must be stopped; the archive must still
/// have <paramref name="Sha256"/> (lowercase hex). An archive no longer on the node is fetched from
/// <paramref name="Remote"/> first (and kept).
/// </summary>
/// <param name="Clean">
/// First delete the files the manifest's backup rules select that the backup does not have (files made after it);
/// otherwise they stay. Only for agents that announce <see cref="AgentCapabilities.CleanRestore"/>: older ones ignore it.
/// </param>
/// <param name="SizeBytes">
/// The archive's size when the node made it. With a <paramref name="Remote"/> copy, the download stops as soon as the
/// storage sends more than this, so a bucket serving a huge object cannot fill the node's disk. Null (a panel that does
/// not send it, or a backup without a recorded size): no limit.
/// </param>
public sealed record RestoreBackupCommand(Guid ServerId, Guid BackupId, string Sha256, RemoteBackupTarget? Remote = null, bool Clean = false,
    long? SizeBytes = null) : AgentCommand
{
    public const string Type = "server.backup_restore";
}

/// <summary>
/// Deletes a backup's archive from the node, and its copy from <paramref name="Remote"/>; one that is already gone is
/// <see cref="AckStatus.AlreadyInState"/>.
/// </summary>
public sealed record DeleteBackupCommand(Guid ServerId, Guid BackupId, RemoteBackupTarget? Remote = null) : AgentCommand
{
    public const string Type = "server.backup_delete";
}

/// <summary>
/// Sends a backup's archive to the panel like <see cref="SendFileCommand"/> (the ack carries a <see cref="FileEntryResult"/>),
/// with <c>PUT</c> to <see cref="AgentProtocol.TransferPath"/>; from <paramref name="Remote"/> when the node no longer has it.
/// </summary>
public sealed record SendBackupCommand(Guid ServerId, Guid BackupId, string Ticket, RemoteBackupTarget? Remote = null) : AgentCommand
{
    public const string Type = "server.backup_send";

    // The ticket is a secret: it must never reach a log through ToString.
    protected override bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"ServerId = {ServerId}, BackupId = {BackupId}, Ticket = ***, Remote = {Remote}");
        return true;
    }
}

/// <summary>What a command (or an operation, in <see cref="OperationCompleted"/>) answers besides being accepted.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FileListResult), "files.list")]
[JsonDerivedType(typeof(FileEntryResult), "files.entry")]
[JsonDerivedType(typeof(BackupResult), "backups.created")]
[JsonDerivedType(typeof(AddonsResult), "addons.changed")]
[JsonDerivedType(typeof(PortsInUseResult), "node.ports_in_use")]
public abstract record CommandResult;

/// <summary>
/// Addons an operation installed, updated (an addon installed again after a game update) or removed (no release).
/// </summary>
public sealed record AddonsResult(IReadOnlyList<AddonRelease> Addons) : CommandResult;

/// <summary>The ports in use on the node's machine (<see cref="ListPortsInUseCommand"/>), each once, at most <see cref="MaxPorts"/>.</summary>
/// <param name="Truncated">There were more: the lowest numbers are the ones listed.</param>
public sealed record PortsInUseResult(IReadOnlyList<PortInUse> Ports, bool Truncated = false) : CommandResult
{
    public const int MaxPorts = 4096;
}

/// <param name="Address">The local address it is bound to: 0.0.0.0 or :: for every interface.</param>
/// <param name="Protocol"><c>tcp</c> (listening) or <c>udp</c>.</param>
public sealed record PortInUse(string Address, int Port, string Protocol);

/// <param name="Release">The release installed (a GitHub tag); null when the addon was removed.</param>
public sealed record AddonRelease(string Key, string? Release);

/// <summary>A backup the node made.</summary>
/// <param name="Size">Bytes of the archive.</param>
/// <param name="FileCount">Files in it.</param>
/// <param name="SkippedFiles">Files left out because the game held them open (a backup of a running server), or because no path can name them.</param>
/// <param name="Sha256">The archive's SHA-256, lowercase hex: a restore checks it first.</param>
/// <param name="RemoteCopied">The archive was copied to the command's remote storage.</param>
/// <param name="RemoteError">Why the copy to remote storage failed (the backup itself is fine on the node).</param>
/// <param name="Capture">How the files were read (null from agents older than Phase 15).</param>
public sealed record BackupResult(long Size, int FileCount, int SkippedFiles, string Sha256, bool RemoteCopied = false, string? RemoteError = null,
    BackupCapture? Capture = null) : CommandResult;

/// <summary>How a backup read the server's files (docs/architecture/05, "Backups").</summary>
/// <param name="SnapshotError">
/// Why a running game's files were read live rather than from a snapshot (<see cref="BackupCaptureMode.Live"/>): one of
/// <see cref="BackupSnapshotErrors"/>.
/// </param>
/// <param name="Save">How the game answered when asked to save first; null when it was not asked (it was not running, or its manifest has no save).</param>
public sealed record BackupCapture(BackupCaptureMode Mode, string? SnapshotError = null, GameSaveOutcome? Save = null);

public enum BackupCaptureMode
{
    /// <summary>The game was not running: nothing was writing the files.</summary>
    Stopped,

    /// <summary>Read from a snapshot of the volume: every file as it was at the same moment.</summary>
    Snapshot,

    /// <summary>Read while the game ran, file by file: files it wrote meanwhile may be half-written, files it held open are left out.</summary>
    Live,
}

/// <summary>What happened when a running game was asked to save before a backup (the manifest's <c>backup.save</c>).</summary>
public enum GameSaveOutcome
{
    /// <summary>The game wrote the line that says the save ended.</summary>
    Confirmed,

    /// <summary>The game has no such line: the backup waited the time its manifest gives.</summary>
    Waited,

    /// <summary>The game did not confirm in time, or did not take the commands: the backup went on with what was on disk.</summary>
    Unconfirmed,
}

public enum AckStatus
{
    Accepted,
    AlreadyInState,
    Rejected,
    Busy,
    UnsupportedCommand,
    InvalidSignature,
}

/// <summary>The agent's answer: accepted or not (long work reports completion through events).</summary>
/// <param name="Result">What the command answers, for commands that answer something (null from older agents).</param>
public sealed record CommandAck(Guid CommandId, AckStatus Status, string? ErrorCode, string? Message, CommandResult? Result = null);

/// <summary>Capabilities an agent announces in its Hello; the panel never sends a command the agent did not announce.</summary>
public static class AgentCapabilities
{
    public const string Ping = PingCommand.Type;
    public const string RotateCredential = RotateCredentialCommand.Type;
    public const string UpdatePanelKeys = UpdatePanelKeysCommand.Type;
    public const string ProvisionServer = ProvisionServerCommand.Type;
    public const string InstallServer = InstallServerCommand.Type;
    public const string StartServer = StartServerCommand.Type;
    public const string StopServer = StopServerCommand.Type;
    public const string RestartServer = RestartServerCommand.Type;
    public const string KillServer = KillServerCommand.Type;
    public const string DeleteServer = DeleteServerCommand.Type;
    public const string UpdateServer = UpdateServerCommand.Type;

    /// <summary>The agent checks for game updates (<see cref="CheckGameUpdateCommand"/>) and reports them (<see cref="IAgentHub.GameUpdateChecked"/>).</summary>
    public const string CheckGameUpdate = CheckGameUpdateCommand.Type;
    public const string ApplyServerSpec = ApplyServerSpecCommand.Type;
    public const string CancelOperation = CancelOperationCommand.Type;
    public const string ConsoleInput = SendConsoleInputCommand.Type;
    public const string ConsoleSubscribe = SubscribeConsoleCommand.Type;
    public const string ConsoleUnsubscribe = UnsubscribeConsoleCommand.Type;
    public const string ListFiles = ListFilesCommand.Type;
    public const string CreateDirectory = CreateDirectoryCommand.Type;
    public const string MoveFile = MoveFileCommand.Type;
    public const string DeleteFile = DeleteFileCommand.Type;
    public const string SendFile = SendFileCommand.Type;
    public const string ReceiveFile = ReceiveFileCommand.Type;
    public const string CreateBackup = CreateBackupCommand.Type;
    public const string RestoreBackup = RestoreBackupCommand.Type;
    public const string DeleteBackup = DeleteBackupCommand.Type;
    public const string SendBackup = SendBackupCommand.Type;

    /// <summary>
    /// Not a command: the agent can install Pulsar's test game server (the <c>pulsar-test</c> manifest). Only agents
    /// started with a test game server directory announce it (development and CI).
    /// </summary>
    public const string TestGame = "server.test_game";

    /// <summary>Not a command: the agent manages Windows Firewall rules for its servers (its administrator enabled it).</summary>
    public const string Firewall = "node.firewall";

    /// <summary>
    /// Not a command: the agent runs each game in a host process of its own, so games keep running while the agent
    /// restarts or is updated (Phase 14).
    /// </summary>
    public const string ServerHosts = "server.hosts";

    /// <summary>Not a command: the agent copies backups to S3-compatible storage (<see cref="RemoteBackupTarget"/>).</summary>
    public const string RemoteBackups = "backups.remote";

    /// <summary>Not a command: the agent honours <see cref="RestoreBackupCommand.Clean"/> (Phase 15).</summary>
    public const string CleanRestore = "backups.clean_restore";

    public const string InstallAddon = InstallAddonCommand.Type;
    public const string RemoveAddon = RemoveAddonCommand.Type;

    /// <summary>
    /// Not a command: the agent reads manifests with Pulsar 1.0.5's game settings (number variables, keys kept when empty,
    /// conditions on any variable); an older one refuses them, so the panel gives them only to agents that announce this.
    /// </summary>
    public const string GameSettings = "manifest.game_settings";

    /// <summary>
    /// Not a command: the agent writes Pulsar 1.0.7's configuration formats (<c>json</c>, <c>space</c>); an older one refuses
    /// manifests that use them.
    /// </summary>
    public const string MoreConfigFormats = "manifest.more_config_formats";

    /// <summary>
    /// Not a command: the agent installs Minecraft: Bedrock Edition (Pulsar 1.0.8); an older one refuses manifests that do.
    /// </summary>
    public const string MinecraftBedrock = "manifest.minecraft_bedrock";

    /// <summary>
    /// Not a command: the agent reads manifests with console commands for the players (Pulsar 1.0.9,
    /// <c>commands</c>); an older one refuses them.
    /// </summary>
    public const string ManifestCommands = "manifest.commands";

    /// <summary>Not a command: the agent enforces <see cref="ServerRuntimeSpec.Limits"/> (CPU and memory caps of a server's Job).</summary>
    public const string Limits = "server.limits";

    /// <summary>Not a command: the agent logs SteamCMD in with a <see cref="Contracts.SteamLogin"/> (Phase 13).</summary>
    public const string SteamAccounts = "steam.login";

    /// <summary>
    /// The agent forgets the cached login of a deleted Steam account by command only (sent again for those its Hello
    /// names in <see cref="AgentHello.CachedSteamLogins"/>).
    /// </summary>
    public const string ForgetSteamLogin = ForgetSteamLoginCommand.Type;

    /// <summary>
    /// The agent presents its node key as a TLS client certificate and can move to the panel's agents' address
    /// (Phase 16): it gets <see cref="UseAgentsAddressCommand"/> and follows a signed <see cref="AgentError.AgentsAddress"/>.
    /// </summary>
    public const string UseAgentsAddress = UseAgentsAddressCommand.Type;

    /// <summary>
    /// The agent serves SFTP's needs (Phase 17): paged listings (<see cref="ListFilesCommand.Paged"/>),
    /// <see cref="StatFileCommand"/>, downloads from an offset (<see cref="SendFileCommand.Offset"/>), streamed and appended
    /// uploads (<see cref="ReceiveFileCommand.Streamed"/>, <see cref="ReceiveFileCommand.Append"/>), renames that replace
    /// (<see cref="MoveFileCommand.Replace"/>) and <see cref="SetFileModifiedCommand"/>. The panel uses none of them with an
    /// agent that does not announce it: an older agent would ignore the new fields and do something else than asked.
    /// </summary>
    public const string SftpFiles = "server.files_sftp";

    /// <summary>Announced with <see cref="SftpFiles"/>, which the panel checks: the commands SFTP added.</summary>
    public const string StatFile = StatFileCommand.Type;

    /// <inheritdoc cref="StatFile"/>
    public const string SetFileModified = SetFileModifiedCommand.Type;

    /// <summary>
    /// The agent updates Pulsar on its machine when told (<see cref="UpdatePulsarCommand"/>, Phase 19): announced only by
    /// an agent the installer or the setups installed as a service, not by one the agent MSI maintains.
    /// </summary>
    public const string UpdatePulsar = UpdatePulsarCommand.Type;
    public const string ListPortsInUse = ListPortsInUseCommand.Type;

    /// <summary>Not a command: the panel runs on this agent's machine, so an update of the agent there updates the panel too.</summary>
    public const string PanelHost = "node.panel_host";

    /// <summary>The capability an agent must announce to receive <paramref name="command"/>.</summary>
    public static string Of(AgentCommand command) => command switch
    {
        PingCommand => Ping,
        ListPortsInUseCommand => ListPortsInUse,
        RotateCredentialCommand => RotateCredential,
        UpdatePanelKeysCommand => UpdatePanelKeys,
        ProvisionServerCommand => ProvisionServer,
        InstallServerCommand => InstallServer,
        StartServerCommand => StartServer,
        StopServerCommand => StopServer,
        RestartServerCommand => RestartServer,
        KillServerCommand => KillServer,
        DeleteServerCommand => DeleteServer,
        UpdateServerCommand => UpdateServer,
        CheckGameUpdateCommand => CheckGameUpdate,
        ApplyServerSpecCommand => ApplyServerSpec,
        CancelOperationCommand => CancelOperation,
        SendConsoleInputCommand => ConsoleInput,
        SubscribeConsoleCommand => ConsoleSubscribe,
        UnsubscribeConsoleCommand => ConsoleUnsubscribe,
        ListFilesCommand => ListFiles,
        CreateDirectoryCommand => CreateDirectory,
        MoveFileCommand => MoveFile,
        DeleteFileCommand => DeleteFile,
        SendFileCommand => SendFile,
        ReceiveFileCommand => ReceiveFile,
        StatFileCommand => StatFile,
        SetFileModifiedCommand => SetFileModified,
        CreateBackupCommand => CreateBackup,
        RestoreBackupCommand => RestoreBackup,
        DeleteBackupCommand => DeleteBackup,
        SendBackupCommand => SendBackup,
        InstallAddonCommand => InstallAddon,
        RemoveAddonCommand => RemoveAddon,
        ForgetSteamLoginCommand => ForgetSteamLogin,
        UseAgentsAddressCommand => UseAgentsAddress,
        UpdatePulsarCommand => UpdatePulsar,
        null => throw new ArgumentNullException(nameof(command)),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "Command without a capability."),
    };
}
