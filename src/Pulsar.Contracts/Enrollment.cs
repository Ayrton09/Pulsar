namespace Pulsar.Contracts;

/// <summary>A panel signing key. <see cref="KeyId"/> is its fingerprint (<see cref="ProtocolCrypto.Fingerprint"/>).</summary>
public sealed record PanelKey(string KeyId, byte[] PublicKey);

/// <summary><c>GET /agent/v1/panel-identity</c>: the keys the panel signs with. The agent pins them at enrollment.</summary>
public sealed record PanelIdentityResponse(IReadOnlyList<PanelKey> Keys);

/// <summary>What the node reports about itself (enrollment and every Hello).</summary>
public sealed record NodeInventory(
    string Hostname,
    string OsDescription,
    string OsBuild,
    string? CpuModel,
    int LogicalCores,
    long MemoryTotalBytes,
    string? ServersRoot);

/// <summary>
/// <c>POST /agent/v1/enroll</c>. <see cref="Proof"/> signs
/// <c>SignedData(Purposes.Enroll, token, publicKey)</c> with the new private key, proving the node holds it.
/// </summary>
public sealed record EnrollRequest(
    string Token,
    byte[] PublicKey,
    string KeyStorage,
    string AgentVersion,
    NodeInventory Inventory,
    byte[] Proof);

/// <summary>
/// Payload of the panel's answer to an enrollment, delivered as a <see cref="SignedMessage"/> so the agent can check
/// it came from the panel key it pinned.
/// </summary>
public sealed record EnrollmentResult(Guid NodeId, Guid CredentialId, string PanelOrigin, IReadOnlyList<PanelKey> PanelKeys);

/// <summary>
/// Bytes signed by a panel key. The signature covers <c>SignedData(purpose, Payload)</c>; the payload is verified
/// before it is deserialized.
/// </summary>
public sealed record SignedMessage(string KeyId, byte[] Payload, byte[] Signature);

/// <summary><c>POST /agent/v1/auth/challenge</c>.</summary>
public sealed record ChallengeRequest(Guid NodeId, Guid CredentialId);

/// <summary>
/// A single-use nonce. <see cref="PanelOrigin"/> is part of the signed data, so a response obtained by another
/// panel (or a relay) is useless here; the agent refuses origins other than the panel it was enrolled with.
/// </summary>
public sealed record ChallengeResponse(Guid ChallengeId, byte[] Nonce, string PanelOrigin);

/// <summary>
/// <c>POST /agent/v1/auth/token</c>. <see cref="Signature"/> signs
/// <c>SignedData(Purposes.Authenticate, nodeId, credentialId, nonce, panelOrigin)</c>.
/// </summary>
public sealed record TokenRequest(Guid ChallengeId, byte[] Signature);

/// <summary>A session for the agent hub only. It ends at <see cref="ExpiresAt"/>, on revocation, or on panel restart.</summary>
public sealed record TokenResponse(string AccessToken, Guid SessionId, DateTimeOffset ExpiresAt);

/// <summary>Error body of the agent endpoints.</summary>
public sealed record AgentError(string Code, string Message);
