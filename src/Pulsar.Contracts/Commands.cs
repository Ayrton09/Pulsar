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

    /// <summary>The capability an agent must announce to receive <paramref name="command"/>.</summary>
    public static string Of(AgentCommand command) => command switch
    {
        PingCommand => Ping,
        RotateCredentialCommand => RotateCredential,
        UpdatePanelKeysCommand => UpdatePanelKeys,
        null => throw new ArgumentNullException(nameof(command)),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "Command without a capability."),
    };
}
