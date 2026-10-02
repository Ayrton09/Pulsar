using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

/// <summary>The panel's signing keys (docs/architecture/03). Implemented in Infrastructure; the private key never leaves it.</summary>
public interface IPanelKeyRing
{
    /// <summary>Keys agents must accept: the active one first, then any being retired.</summary>
    Task<IReadOnlyList<PanelKey>> GetPublicKeysAsync(CancellationToken cancellationToken);

    /// <summary>Signs <paramref name="payload"/> for <paramref name="purpose"/> with the active key.</summary>
    Task<SignedMessage> SignAsync(string purpose, byte[] payload, CancellationToken cancellationToken);

    /// <summary>
    /// Signs with a key the agent trusts: the active key when it is among <paramref name="trustedKeyIds"/>, otherwise
    /// the newest retiring one that is (an agent that missed a panel key rotation). Null when the agent trusts none of
    /// them; a null <paramref name="trustedKeyIds"/> (agent did not say) means the active key.
    /// </summary>
    Task<SignedMessage?> SignForAsync(string purpose, byte[] payload, IReadOnlyCollection<string>? trustedKeyIds, CancellationToken cancellationToken);

    /// <summary>A new key pair with its private half protected for storage. Not saved: the caller adds it.</summary>
    PanelSigningKey CreateKey();

    /// <summary>Forgets the cached keys, so the next use reads them again (after a rotation or a retirement).</summary>
    void Reload();
}

/// <summary>Live state of a connected agent (memory only, rebuilt from every Hello).</summary>
public sealed record AgentConnectionInfo(
    NodeId NodeId,
    Guid SessionId,
    DateTimeOffset ConnectedAt,
    string AgentVersion,
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    NodeMetrics? LastMetrics,
    IReadOnlyList<string>? PinnedPanelKeys = null);

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
    /// <param name="operationId">For commands that start an operation; the agent reports its end with this id.</param>
    Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null);

    /// <summary>
    /// Sends the current panel keys to connected agents that pin a different set (one node, or all when
    /// <paramref name="node"/> is null), signed with a key each agent trusts. Returns how many agents accepted them.
    /// </summary>
    Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken);
}
