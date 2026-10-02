using Microsoft.Extensions.Logging;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Commands;

/// <summary>
/// Runs verified commands. The ack only says whether the command was accepted; long work reports its completion
/// through events (later phases).
/// </summary>
public sealed partial class CommandDispatcher(ILogger<CommandDispatcher> logger)
{
    /// <summary>Command types this agent handles, announced as capabilities in its Hello.</summary>
    public IReadOnlySet<string> SupportedCommands { get; } = new HashSet<string>(StringComparer.Ordinal) { AgentCapabilities.Ping };

    public CommandAck Execute(CommandVerifier verifier, SignedMessage message)
    {
        ArgumentNullException.ThrowIfNull(verifier);
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
                return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null);
            default:
                // Announced as supported but not wired here: a bug, never a reason to act on the command.
                return new CommandAck(envelope.CommandId, AckStatus.UnsupportedCommand, "command.unsupported", null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused command {CommandId}: {Status} ({ErrorCode})")]
    private partial void LogRefused(Guid commandId, AckStatus status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ping {CommandId} from the panel (user {ActorUserId})")]
    private partial void LogPing(Guid commandId, Guid? actorUserId);
}
