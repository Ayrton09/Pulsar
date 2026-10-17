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

/// <summary>How a request reached the panel: what a node code carries besides the token and the panel key (Phase 18).</summary>
/// <param name="Url">The panel's origin as the request reached it (<see cref="NodeCode.IsValidUrl"/>).</param>
/// <param name="CertificateSha256">
/// The TLS certificate the panel itself served that request (<see cref="NodeCode.CertificateHash"/>); null when it did not
/// serve one, or one that clients may not see (a proxy in front of it).
/// </param>
public sealed record PanelAddress(string Url, string? CertificateSha256);

/// <summary>What a node's setup is told by the API host, which knows the request (Phase 18).</summary>
public interface INodeSetup
{
    /// <summary>
    /// The panel's address as the current request reached it; null outside a request, or when an agent could not use it
    /// (plain HTTP other than on the panel's machine).
    /// </summary>
    PanelAddress? RequestAddress { get; }

    /// <summary>
    /// The lowercase hex SHA-256 of the agent's setup (<c>PulsarAgentSetup.exe</c>) the panel serves for download, or null
    /// when it serves none.
    /// </summary>
    ValueTask<string?> AgentSetupSha256Async(CancellationToken cancellationToken);
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
    /// Closes the node's connections whose session ended (a key superseded by a rotation), on the hub and on the agents'
    /// endpoint alike, and leaves those of sessions still active.
    /// </summary>
    void DisconnectEnded(NodeId node);

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
