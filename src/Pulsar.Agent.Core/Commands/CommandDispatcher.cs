using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Agent.Core.Updates;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Commands;

/// <summary>What commands can ask of the agent itself (implemented by the connection, which owns the configuration).</summary>
public interface IAgentControl
{
    /// <summary>Starts a node key rotation; false when one is already under way.</summary>
    bool RequestCredentialRotation();

    /// <summary>Replaces the pinned panel keys; false when they are already these.</summary>
    bool ReplacePanelKeys(IReadOnlyList<PanelKey> keys);

    /// <summary>
    /// Signs in at the panel's agents' address (<paramref name="port"/> on the host the agent reaches the panel by, or the
    /// operator's <paramref name="url"/>) and, if that works, keeps it, with the address in use as fallback (Phase 16).
    /// <c>Rejected</c> with <c>agent.address_unreachable</c> when it cannot sign in there: nothing changes.
    /// </summary>
    Task<CommandAck> UseAgentsAddressAsync(Guid commandId, int port, Uri? url);
}

/// <summary>
/// Runs verified commands. The ack only says whether the command was accepted; long work reports its completion
/// through events.
/// </summary>
/// <param name="steamLogins">The cached Steam logins (<see cref="ForgetSteamLoginCommand"/>); null: an agent without servers.</param>
/// <param name="updater">Updates Pulsar on this machine (<see cref="UpdatePulsarCommand"/>); null: an agent that never does.</param>
/// <param name="portsInUse">The ports in use on this machine (<see cref="ListPortsInUseCommand"/>); null: the machine's own.</param>
public sealed partial class CommandDispatcher(ILogger<CommandDispatcher> logger, ServerHost? servers = null, SteamLoginStore? steamLogins = null,
    AgentUpdater? updater = null, Func<PortsInUseResult>? portsInUse = null)
{
    /// <summary>Command types this agent handles (and features it has), announced as capabilities in its Hello.</summary>
    public IReadOnlySet<string> SupportedCommands { get; } = new HashSet<string>(
        [
            AgentCapabilities.Ping, AgentCapabilities.RotateCredential, AgentCapabilities.UpdatePanelKeys, AgentCapabilities.UseAgentsAddress,
            AgentCapabilities.ListPortsInUse,
            .. servers?.Capabilities ?? [],
            .. steamLogins is null ? [] : new[] { AgentCapabilities.ForgetSteamLogin },
            .. updater?.Capabilities ?? [],
        ],
        StringComparer.Ordinal);

    public async Task<CommandAck> ExecuteAsync(CommandVerifier verifier, SignedMessage message, IAgentControl control)
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
            case ListPortsInUseCommand:
                return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null, (portsInUse ?? PortsInUse.List)());
            case RotateCredentialCommand:
                LogRotationRequested(envelope.CommandId, envelope.ActorUserId);
                return Ack(envelope, control.RequestCredentialRotation() ? AckStatus.Accepted : AckStatus.AlreadyInState);
            case UpdatePulsarCommand update when SupportedCommands.Contains(AgentCapabilities.UpdatePulsar):
                LogUpdateRequested(envelope.CommandId, update.Version, envelope.ActorUserId);
                return updater!.Start(envelope.CommandId, update);
            case ForgetSteamLoginCommand forget when steamLogins is not null:
                var forgotten = steamLogins.Forget(forget.AccountId);
                LogSteamLoginForgotten(envelope.CommandId, forget.AccountId, forgotten);
                return Ack(envelope, forgotten ? AckStatus.Accepted : AckStatus.AlreadyInState);
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
            case UseAgentsAddressCommand use:
                var url = use.Url is null ? null : PanelAddress.TryParseHttpsOrigin(use.Url);
                if (use.Port is <= 0 or > 65535 || (use.Url is not null && url is null))
                {
                    LogRefused(envelope.CommandId, AckStatus.Rejected, "command.invalid_agents_address");
                    return new CommandAck(envelope.CommandId, AckStatus.Rejected, "command.invalid_agents_address",
                        "The agents' address needs a port, and an address only as an https origin.");
                }

                var moved = await control.UseAgentsAddressAsync(envelope.CommandId, use.Port, url);
                LogAgentsAddress(envelope.CommandId, use.Port, url, moved.Status, moved.ErrorCode);
                return moved;
            // Whatever the server host announces goes to it: one list of server commands, not two to keep in step.
            case var command when servers is not null && servers.Capabilities.Contains(AgentCapabilities.Of(command), StringComparer.Ordinal):
                var ack = await servers.ExecuteAsync(envelope);
                LogServerCommand(envelope.CommandId, envelope.Command.GetType().Name, ack.Status, ack.ErrorCode, envelope.ActorUserId);
                return ack;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Update of Pulsar to {Version} requested by the panel ({CommandId}, user {ActorUserId})")]
    private partial void LogUpdateRequested(Guid commandId, string version, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused command {CommandId}: {Status} ({ErrorCode})")]
    private partial void LogRefused(Guid commandId, AckStatus status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Command} {CommandId} from the panel (user {ActorUserId}): {Status} {ErrorCode}")]
    private partial void LogServerCommand(Guid commandId, string command, AckStatus status, string? errorCode, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ping {CommandId} from the panel (user {ActorUserId})")]
    private partial void LogPing(Guid commandId, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Key rotation requested by the panel ({CommandId}, user {ActorUserId})")]
    private partial void LogRotationRequested(Guid commandId, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Panel keys from command {CommandId}: {Keys} (changed: {Changed})")]
    private partial void LogPanelKeys(Guid commandId, string keys, bool changed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agents' address from the panel ({CommandId}): port {Port}, address {Url}: {Status} {ErrorCode}")]
    private partial void LogAgentsAddress(Guid commandId, int port, Uri? url, AckStatus status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam account {AccountId} deleted in the panel ({CommandId}): cached login forgotten: {Forgotten}")]
    private partial void LogSteamLoginForgotten(Guid commandId, Guid accountId, bool forgotten);
}
