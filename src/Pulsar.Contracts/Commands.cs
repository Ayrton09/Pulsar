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
[JsonDerivedType(typeof(CreateBackupCommand), CreateBackupCommand.Type)]
[JsonDerivedType(typeof(RestoreBackupCommand), RestoreBackupCommand.Type)]
[JsonDerivedType(typeof(DeleteBackupCommand), DeleteBackupCommand.Type)]
[JsonDerivedType(typeof(SendBackupCommand), SendBackupCommand.Type)]
[JsonDerivedType(typeof(InstallAddonCommand), InstallAddonCommand.Type)]
[JsonDerivedType(typeof(RemoveAddonCommand), RemoveAddonCommand.Type)]
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
/// Operation (the envelope carries its id): creates or repairs the server's directory and Windows account and stores
/// its runtime spec. Completion is reported with <see cref="OperationCompleted"/>.
/// </summary>
public sealed record ProvisionServerCommand(Guid ServerId, ServerRuntimeSpec Spec) : AgentCommand
{
    public const string Type = "server.provision";
}

/// <summary>Operation: runs the manifest's install steps.</summary>
public sealed record InstallServerCommand(Guid ServerId) : AgentCommand
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
public sealed record UpdateServerCommand(Guid ServerId, bool Validate) : AgentCommand
{
    public const string Type = "server.update";
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
public sealed record RemoveAddonCommand(Guid ServerId, string AddonKey) : AgentCommand
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
public sealed record ListFilesCommand(Guid ServerId, string Path) : AgentCommand
{
    public const string Type = "server.files_list";
}

/// <summary>Creates a directory and any missing parents.</summary>
public sealed record CreateDirectoryCommand(Guid ServerId, string Path) : AgentCommand
{
    public const string Type = "server.files_create_directory";
}

/// <summary>Renames or moves a file, directory or link inside the server directory; never replaces anything.</summary>
public sealed record MoveFileCommand(Guid ServerId, string Path, string Destination) : AgentCommand
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
public sealed record SendFileCommand(Guid ServerId, string Path, string Ticket, long MaxSize) : AgentCommand
{
    public const string Type = "server.files_send";

    // The ticket is a secret: it must never reach a log through ToString.
    protected override bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"ServerId = {ServerId}, Path = {Path}, Ticket = ***, MaxSize = {MaxSize}");
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
public sealed record ReceiveFileCommand(Guid ServerId, string Path, string Ticket, long Size, bool Overwrite, string? ExpectedVersion)
    : AgentCommand
{
    public const string Type = "server.files_receive";

    // The ticket is a secret: it must never reach a log through ToString.
    protected override bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"ServerId = {ServerId}, Path = {Path}, Ticket = ***, Size = {Size}, Overwrite = {Overwrite}, ExpectedVersion = {ExpectedVersion}");
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
/// Operation: archives the server's files (the manifest's <c>backup</c> globs) into a zip kept on the node
/// (docs/architecture/03, "Backups"), then copies it to <paramref name="Remote"/> when there is one. The server may keep
/// running. The result carries a <see cref="BackupResult"/>.
/// </summary>
public sealed record CreateBackupCommand(Guid ServerId, Guid BackupId, RemoteBackupTarget? Remote = null) : AgentCommand
{
    public const string Type = "server.backup_create";
}

/// <summary>
/// Operation: puts the backup's files back in the server directory (files it does not have stay as they are). The
/// server must be stopped; the archive must still have <paramref name="Sha256"/> (lowercase hex). An archive no longer
/// on the node is fetched from <paramref name="Remote"/> first (and kept).
/// </summary>
public sealed record RestoreBackupCommand(Guid ServerId, Guid BackupId, string Sha256, RemoteBackupTarget? Remote = null) : AgentCommand
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
public abstract record CommandResult;

/// <summary>
/// Addons an operation installed, updated (an addon installed again after a game update) or removed (no release).
/// </summary>
public sealed record AddonsResult(IReadOnlyList<AddonRelease> Addons) : CommandResult;

/// <param name="Release">The release installed (a GitHub tag); null when the addon was removed.</param>
public sealed record AddonRelease(string Key, string? Release);

/// <summary>A backup the node made.</summary>
/// <param name="Size">Bytes of the archive.</param>
/// <param name="FileCount">Files in it.</param>
/// <param name="SkippedFiles">Files left out because the game held them open (a backup of a running server).</param>
/// <param name="Sha256">The archive's SHA-256, lowercase hex: a restore checks it first.</param>
/// <param name="RemoteCopied">The archive was copied to the command's remote storage.</param>
/// <param name="RemoteError">Why the copy to remote storage failed (the backup itself is fine on the node).</param>
public sealed record BackupResult(long Size, int FileCount, int SkippedFiles, string Sha256, bool RemoteCopied = false, string? RemoteError = null)
    : CommandResult;

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

    /// <summary>Not a command: the agent copies backups to S3-compatible storage (<see cref="RemoteBackupTarget"/>).</summary>
    public const string RemoteBackups = "backups.remote";

    public const string InstallAddon = InstallAddonCommand.Type;
    public const string RemoveAddon = RemoveAddonCommand.Type;

    /// <summary>Not a command: the agent enforces <see cref="ServerRuntimeSpec.Limits"/> (CPU and memory caps of a server's Job).</summary>
    public const string Limits = "server.limits";

    /// <summary>The capability an agent must announce to receive <paramref name="command"/>.</summary>
    public static string Of(AgentCommand command) => command switch
    {
        PingCommand => Ping,
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
        CreateBackupCommand => CreateBackup,
        RestoreBackupCommand => RestoreBackup,
        DeleteBackupCommand => DeleteBackup,
        SendBackupCommand => SendBackup,
        InstallAddonCommand => InstallAddon,
        RemoveAddonCommand => RemoveAddon,
        null => throw new ArgumentNullException(nameof(command)),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "Command without a capability."),
    };
}
