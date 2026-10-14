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
    byte[] Proof)
{
    // The enrollment token is a secret: it must never reach a log through ToString.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"Token = ***, PublicKey = {PublicKey}, KeyStorage = {KeyStorage}, AgentVersion = {AgentVersion}, Inventory = {Inventory}, Proof = {Proof}");
        return true;
    }
}

/// <summary>
/// Payload of the panel's answer to an enrollment, delivered as a <see cref="SignedMessage"/> so the agent can check
/// it came from the panel key it pinned.
/// </summary>
/// <param name="AwaitingApproval">
/// The node was created by a reusable token that requires approval (Phase 14): the agent cannot connect until an
/// administrator approves it in the panel.
/// </param>
public sealed record EnrollmentResult(Guid NodeId, Guid CredentialId, string PanelOrigin, IReadOnlyList<PanelKey> PanelKeys, bool AwaitingApproval = false);

/// <summary>
/// Bytes signed by a panel key. The signature covers <c>SignedData(purpose, Payload)</c>; the payload is verified
/// before it is deserialized.
/// </summary>
public sealed record SignedMessage(string KeyId, byte[] Payload, byte[] Signature);

/// <summary><c>POST /agent/v1/auth/challenge</c>.</summary>
/// <param name="Nonce">
/// Phase 16: random bytes the agent sends with each challenge request (at most 64). A refusal that sends it to the agents'
/// address signs them back (<see cref="AgentsAddress.Nonce"/>), so a refusal recorded once cannot be replayed later, and so
/// does the session the sign-in ends with (<see cref="SessionGrant.Nonce"/>).
/// </param>
public sealed record ChallengeRequest(Guid NodeId, Guid CredentialId, byte[]? Nonce = null);

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
/// <param name="Grant">
/// The session, as a <see cref="SessionGrant"/> signed by a panel key the node pins, for the nonce of the challenge
/// request this sign-in began with. Agents use no session without one that verifies; the panel leaves it out only when
/// the request sent no nonce (an older agent) or the node pins none of its keys.
/// </param>
public sealed record TokenResponse(string AccessToken, Guid SessionId, DateTimeOffset ExpiresAt, SignedMessage? Grant = null)
{
    // The session token is a secret: it must never reach a log through ToString.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"AccessToken = ***, SessionId = {SessionId}, ExpiresAt = {ExpiresAt}, Grant = {Grant}");
        return true;
    }
}

/// <summary>
/// Payload of <see cref="TokenResponse.Grant"/>, signed with <see cref="AgentProtocol.Purposes.SessionGrant"/>: the panel
/// itself opened <paramref name="SessionId"/> for this node and credential, at the sign-in whose challenge request carried
/// <paramref name="Nonce"/> (the agent's, not the panel's challenge nonce). A sign-in answered by whatever sits between the
/// agent and the panel has none the agent accepts, and one recorded earlier answers another nonce. The agent uses no
/// session without one (docs/architecture/03, "Authentication on every connection").
/// </summary>
/// <param name="IssuedAt">
/// The panel's clock when it signed the grant: the agent measures the panel's clock offset from it, against its own clock
/// when the answer arrived (never from what <see cref="HelloAccepted"/> says), so commands' expiry is judged on a time
/// whatever sits on the way cannot choose. Required by agents; null only from panels older than the field.
/// </param>
public sealed record SessionGrant(Guid NodeId, Guid CredentialId, Guid SessionId, DateTimeOffset ExpiresAt, byte[]? Nonce, DateTimeOffset? IssuedAt = null);

/// <summary>Error body of the agent endpoints.</summary>
/// <param name="AgentsAddress">
/// With <c>agent.certificate_required</c> (Phase 16): where this node connects with its client certificate, an
/// <see cref="Contracts.AgentsAddress"/> signed by a panel key the agent pins. Null otherwise (additive).
/// </param>
public sealed record AgentError(string Code, string Message, SignedMessage? AgentsAddress = null);

/// <summary>
/// Payload of <see cref="AgentError.AgentsAddress"/>, signed with <see cref="AgentProtocol.Purposes.AgentsAddress"/>: where
/// one node finds the agents' endpoint, as <see cref="UseAgentsAddressCommand"/> says it (the port on the host the agent
/// reached, or the operator's <paramref name="Url"/>), bound to that node and to the <paramref name="Nonce"/> of the
/// challenge request it answers.
/// </summary>
public sealed record AgentsAddress(Guid NodeId, int Port, string? Url, byte[]? Nonce);

/// <summary>
/// <c>POST /agent/v1/credentials/rotate</c>, within the current session. <see cref="Proof"/> signs
/// <c>SignedData(Purposes.RotateCredential, nodeId, currentCredentialId, sessionId, publicKey)</c> with the NEW private
/// key; <see cref="CurrentKeyProof"/> signs <c>SignedData(Purposes.AuthorizeRotation, nodeId, currentCredentialId,
/// sessionId, publicKey)</c> with the CURRENT one. The session token alone (a bearer token, which can be copied) never
/// registers a key: the current key does, for this new key, node and session only.
/// </summary>
public sealed record RotateCredentialRequest(byte[] PublicKey, string KeyStorage, byte[] Proof, byte[] CurrentKeyProof);

/// <summary>
/// Payload of the panel's answer to a rotation, delivered as a <see cref="SignedMessage"/>. The new credential is
/// pending: it becomes active (and the current one is superseded) the first time the agent authenticates with it.
/// </summary>
/// <param name="KeyFingerprint">
/// The fingerprint of the key the panel registered (<see cref="ProtocolCrypto.Fingerprint"/>), which binds the answer to
/// the request: the agent refuses an answer for another key. Null from panels older than the field (additive).
/// </param>
public sealed record CredentialRotation(Guid NodeId, Guid CredentialId, string? KeyFingerprint = null);
