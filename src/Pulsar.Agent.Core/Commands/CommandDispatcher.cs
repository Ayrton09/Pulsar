using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Commands;

/// <summary>What commands can ask of the agent itself (implemented by the connection, which owns the configuration).</summary>
public interface IAgentControl
{
    /// <summary>Starts a node key rotation; false when one is already under way.</summary>
    bool RequestCredentialRotation();

    /// <summary>Replaces the pinned panel keys; false when they are already these.</summary>
    bool ReplacePanelKeys(IReadOnlyList<PanelKey> keys);
}

/// <summary>
/// Runs verified commands. The ack only says whether the command was accepted; long work reports its completion
/// through events (later phases).
/// </summary>
public sealed partial class CommandDispatcher(ILogger<CommandDispatcher> logger)
{
    /// <summary>Command types this agent handles, announced as capabilities in its Hello.</summary>
    public IReadOnlySet<string> SupportedCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AgentCapabilities.Ping,
        AgentCapabilities.RotateCredential,
        AgentCapabilities.UpdatePanelKeys,
    };

    public CommandAck Execute(CommandVerifier verifier, SignedMessage message, IAgentControl control)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(control);
        var verification = verifier.Verify(message);
        if (verification.Rejection is { } rejection)
        {
            LogRefused(rejection.CommandId, rejection.Status, rejection.ErrorCode);
            return rejection;
        }

        var envelope = verification.Envelope!;
        switch (envelope.Command)
        {
            case PingCommand:
                LogPing(envelope.CommandId, envelope.ActorUserId);
                return Ack(envelope, AckStatus.Accepted);
            case RotateCredentialCommand:
                LogRotationRequested(envelope.CommandId, envelope.ActorUserId);
                return Ack(envelope, control.RequestCredentialRotation() ? AckStatus.Accepted : AckStatus.AlreadyInState);
            case UpdatePanelKeysCommand update:
                if (!AreValid(update.Keys, verification.SignedBy!))
                {
                    LogRefused(envelope.CommandId, AckStatus.Rejected, "command.invalid_panel_keys");
                    return new CommandAck(envelope.CommandId, AckStatus.Rejected, "command.invalid_panel_keys",
                        "The key list must contain well-formed P-256 keys, including the key that signed it.");
                }

                var changed = control.ReplacePanelKeys(update.Keys);
                var keyIds = string.Join(", ", update.Keys.Select(k => k.KeyId));
                LogPanelKeys(envelope.CommandId, keyIds, changed);
                return Ack(envelope, changed ? AckStatus.Accepted : AckStatus.AlreadyInState);
            default:
                // Announced as supported but not wired here: a bug, never a reason to act on the command.
                return new CommandAck(envelope.CommandId, AckStatus.UnsupportedCommand, "command.unsupported", null);
        }
    }

    /// <summary>
    /// Every key is a P-256 key whose id is its fingerprint, and the list keeps the key that signed it: an update can
    /// never leave the agent unable to verify the panel.
    /// </summary>
    internal static bool AreValid(IReadOnlyList<PanelKey>? keys, string signedBy)
    {
        if (keys is not { Count: > 0 } || !keys.Any(k => k.KeyId == signedBy))
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (key?.PublicKey is null || key.KeyId != ProtocolCrypto.Fingerprint(key.PublicKey))
            {
                return false;
            }

            try
            {
                using var _ = ProtocolCrypto.ImportPublicKey(key.PublicKey);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        return true;
    }

    private static CommandAck Ack(CommandEnvelope envelope, AckStatus status) => new(envelope.CommandId, status, null, null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused command {CommandId}: {Status} ({ErrorCode})")]
    private partial void LogRefused(Guid commandId, AckStatus status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ping {CommandId} from the panel (user {ActorUserId})")]
    private partial void LogPing(Guid commandId, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Key rotation requested by the panel ({CommandId}, user {ActorUserId})")]
    private partial void LogRotationRequested(Guid commandId, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel keys from command {CommandId}: {Keys} (changed: {Changed})")]
    private partial void LogPanelKeys(Guid commandId, string keys, bool changed);
}
