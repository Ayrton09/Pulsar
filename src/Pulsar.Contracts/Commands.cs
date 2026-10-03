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
}

/// <summary>What a command answers besides being accepted (a directory listing, say).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FileListResult), "files.list")]
[JsonDerivedType(typeof(FileEntryResult), "files.entry")]
public abstract record CommandResult;

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

    /// <summary>
    /// Not a command: the agent can install Pulsar's test game server (the <c>pulsar-test</c> manifest). Only agents
    /// started with a test game server directory announce it (development and CI).
    /// </summary>
    public const string TestGame = "server.test_game";

    /// <summary>Not a command: the agent manages Windows Firewall rules for its servers (its administrator enabled it).</summary>
    public const string Firewall = "node.firewall";

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
        null => throw new ArgumentNullException(nameof(command)),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "Command without a capability."),
    };
}
