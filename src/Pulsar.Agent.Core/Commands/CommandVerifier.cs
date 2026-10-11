using System.Text.Json;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Commands;

/// <summary>Outcome of <see cref="CommandVerifier.Verify"/>: a trusted envelope, or the ack that refuses it.</summary>
/// <param name="SignedBy">Key id of the panel key that signed an accepted command.</param>
public sealed record CommandVerification(CommandEnvelope? Envelope, CommandAck? Rejection, string? SignedBy = null)
{
    public static CommandVerification Accepted(CommandEnvelope envelope, string signedBy) => new(envelope, null, signedBy);

    public static CommandVerification Refused(Guid commandId, AckStatus status, string code, string message) =>
        new(null, new CommandAck(commandId, status, code, message));
}

/// <summary>
/// Checks a signed command before anything acts on it (docs/architecture/03), in this order and stopping at the first
/// failure: pinned panel key, signature (before deserializing), recipient node and session, anti-replay window,
/// validity period, protocol version and command type. One verifier per hub session: a new session restarts the
/// sequence.
/// </summary>
/// <param name="pinnedKeys">Read on every command: a panel key update applies to the next command.</param>
public sealed class CommandVerifier(
    Guid nodeId,
    Guid sessionId,
    Func<IReadOnlyList<PanelKey>> pinnedKeys,
    IReadOnlySet<string> supportedCommands,
    TimeProvider clock)
{
    public CommandVerifier(Guid nodeId, Guid sessionId, IReadOnlyList<PanelKey> pinnedKeys, IReadOnlySet<string> supportedCommands, TimeProvider clock)
        : this(nodeId, sessionId, () => pinnedKeys, supportedCommands, clock)
    {
    }

    /// <summary>Accepted difference between the panel's clock and ours, on top of the measured offset.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(30);

    private readonly ReplayWindow _replay = new();

    private readonly TaskCompletionSource _clockMeasured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _panelClockOffset;

    /// <summary>
    /// Panel time minus local time, measured at Hello. Expiry is judged on the panel's clock. Setting it lets the commands
    /// waiting for it through (<see cref="WaitsForClock"/>).
    /// </summary>
    public TimeSpan PanelClockOffset
    {
        get => _panelClockOffset;
        set
        {
            _panelClockOffset = value;
            _clockMeasured.TrySetResult();
        }
    }

    /// <summary>
    /// Commands wait until <see cref="PanelClockOffset"/> is set (a hub session's verifier: the panel's first commands can
    /// arrive between Hello's answer and the offset being measured from it).
    /// </summary>
    public bool WaitsForClock { get; init; }

    /// <summary>Completes when commands may be verified (see <see cref="WaitsForClock"/>).</summary>
    public Task ClockMeasured => WaitsForClock ? _clockMeasured.Task : Task.CompletedTask;

    /// <summary>The session ended before its Hello was answered: commands waiting for the clock are dropped.</summary>
    public void Abandon() => _clockMeasured.TrySetCanceled();

    public CommandVerification Verify(SignedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var key = pinnedKeys().FirstOrDefault(k => k.KeyId == message.KeyId);
        if (key is null)
        {
            return CommandVerification.Refused(Guid.Empty, AckStatus.InvalidSignature, "command.unknown_key", "Signed with a key this node has not pinned.");
        }

        if (!ProtocolCrypto.VerifyMessage(message, AgentProtocol.Purposes.Command, key.PublicKey))
        {
            return CommandVerification.Refused(Guid.Empty, AckStatus.InvalidSignature, "command.invalid_signature", "The signature does not match.");
        }

        EnvelopeHeader header;
        try
        {
            header = ProtocolJson.Deserialize<EnvelopeHeader>(message.Payload);
        }
        catch (JsonException)
        {
            return CommandVerification.Refused(Guid.Empty, AckStatus.Rejected, "command.malformed", "The envelope cannot be read.");
        }

        if (header.NodeId != nodeId || header.SessionId != sessionId)
        {
            return CommandVerification.Refused(header.CommandId, AckStatus.Rejected, "command.wrong_recipient", "The command is for another node or session.");
        }

        if (!_replay.TryAccept(header.Sequence))
        {
            return CommandVerification.Refused(header.CommandId, AckStatus.Rejected, "command.replayed", "This sequence number was already used or is too old.");
        }

        var panelNow = clock.GetUtcNow() + PanelClockOffset;
        if (panelNow > header.ExpiresAt + ClockTolerance || panelNow + ClockTolerance < header.IssuedAt)
        {
            return CommandVerification.Refused(header.CommandId, AckStatus.Rejected, "command.expired", "The command is outside its validity period.");
        }

        if (header.ProtocolVersion is < AgentProtocol.MinimumSupportedVersion or > AgentProtocol.CurrentVersion)
        {
            return CommandVerification.Refused(header.CommandId, AckStatus.Rejected, "command.unsupported_version",
                $"Protocol version {header.ProtocolVersion} is not supported by this agent.");
        }

        var type = header.Command.ValueKind == JsonValueKind.Object && header.Command.TryGetProperty("$type", out var discriminator)
            && discriminator.ValueKind == JsonValueKind.String ? discriminator.GetString() : null;
        if (type is null || !supportedCommands.Contains(type))
        {
            return CommandVerification.Refused(header.CommandId, AckStatus.UnsupportedCommand, "command.unsupported",
                $"This agent does not handle '{type}'.");
        }

        try
        {
            return CommandVerification.Accepted(ProtocolJson.Deserialize<CommandEnvelope>(message.Payload), key.KeyId);
        }
        catch (JsonException)
        {
            return CommandVerification.Refused(header.CommandId, AckStatus.Rejected, "command.malformed", "The command cannot be read.");
        }
    }

    /// <summary>The envelope without its command, so routing checks run before the command type is resolved.</summary>
    private sealed record EnvelopeHeader(
        int ProtocolVersion,
        Guid CommandId,
        Guid NodeId,
        Guid SessionId,
        long Sequence,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        JsonElement Command);
}
