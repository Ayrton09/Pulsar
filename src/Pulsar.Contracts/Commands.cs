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
public sealed record CommandAck(Guid CommandId, AckStatus Status, string? ErrorCode, string? Message);

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
        null => throw new ArgumentNullException(nameof(command)),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "Command without a capability."),
    };
}
