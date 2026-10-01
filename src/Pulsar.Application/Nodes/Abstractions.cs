using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Nodes;

/// <summary>The panel's signing keys (docs/architecture/03). Implemented in Infrastructure; the private key never leaves it.</summary>
public interface IPanelKeyRing
{
    /// <summary>Keys agents must accept: the active one and any being retired.</summary>
    Task<IReadOnlyList<PanelKey>> GetPublicKeysAsync(CancellationToken cancellationToken);

    /// <summary>Signs <paramref name="payload"/> for <paramref name="purpose"/> with the active key.</summary>
    Task<SignedMessage> SignAsync(string purpose, byte[] payload, CancellationToken cancellationToken);
}

/// <summary>Live state of a connected agent (memory only, rebuilt from every Hello).</summary>
public sealed record AgentConnectionInfo(
    NodeId NodeId,
    Guid SessionId,
    DateTimeOffset ConnectedAt,
    string AgentVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    NodeMetrics? LastMetrics);

/// <summary>The agent hub as seen by application code. Implemented by the API host (SignalR).</summary>
public interface IAgentConnections
{
    AgentConnectionInfo? Find(NodeId node);

    /// <summary>Closes the node's connection now (revocation, disabling, deletion).</summary>
    void Disconnect(NodeId node);

    /// <summary>
    /// Signs and sends a command to a connected agent and waits for its ack. Returns null when the node is not
    /// connected or does not announce the command's capability.
    /// </summary>
    Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken);
}
