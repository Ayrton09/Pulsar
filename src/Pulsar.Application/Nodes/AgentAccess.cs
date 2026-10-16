using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

/// <summary>Errors returned to agents. Deliberately coarse: they must not help an attacker probe tokens or keys.</summary>
public static class AgentErrors
{
    public static DomainError EnrollmentRejected { get; } =
        new("agent.enrollment_rejected", "The enrollment token is invalid, expired or already used, or the request is malformed.");

    public static DomainError AuthenticationRejected { get; } =
        new("agent.authentication_rejected", "The node cannot authenticate: unknown or revoked credential, disabled node, or invalid proof.");

    public static DomainError RotationRejected { get; } =
        new("agent.rotation_rejected", "The new key was not accepted: malformed, already registered, or its proof does not verify.");

    /// <summary>
    /// The panel requires agents to present their node key as a TLS client certificate on its agents' address (Phase 16).
    /// The refusal carries that address, signed for the node, so an agent that can move goes there.
    /// </summary>
    public static DomainError CertificateRequired { get; } =
        new("agent.certificate_required", "This panel requires agents to connect to its agents' address with their node key as a client certificate.");
}

/// <summary>
/// Enrollment and per-connection authentication of agents (docs/architecture/03). Every check that fails gives the
/// agent the same answer; the details go to the panel log and the audit log.
/// </summary>
public sealed partial class AgentAccess(
    IPulsarDbContext db,
    IPanelKeyRing keys,
    AgentChallengeStore challenges,
    AgentSessionStore sessions,
    IAgentConnections connections,
    IAuditLog audit,
    AgentRefusalBudget refusals,
    EnrollmentRefusalBudget enrollmentRefusals,
    IRequestContext requestContext,
    IRowLocks locks,
    AgentTransportPolicy transportPolicy,
    TimeProvider clock,
    ILogger<AgentAccess> logger)
{
    private const int MaxTokenLength = 128;

    /// <summary>The longest nonce an agent may send with a challenge request (it is signed back in a refusal).</summary>
    public const int MaxChallengeNonceLength = 64;

    /// <summary>The SQLSTATE of a unique index violation (PostgreSQL's, the standard one).</summary>
    private const string UniqueViolation = "23505";

    public async Task<Result<SignedMessage>> EnrollAsync(EnrollRequest request, string panelOrigin, AgentTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);
        if (!IsWellFormed(request, out var keyStorage, out var reason))
        {
            return await RejectEnrollmentAsync(null, reason, cancellationToken);
        }

        // Through the agents' endpoint the TLS connection holds the key being enrolled, as every later one will.
        if (transport.ThroughAgentsEndpoint && !transport.PresentsKey(request.PublicKey))
        {
            return await RejectEnrollmentAsync(null, "client_certificate_mismatch", cancellationToken);
        }

        var now = clock.GetUtcNow();
        var hash = NodeEnrollmentToken.Hash(request.Token);
        var token = await db.NodeEnrollmentTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null && await db.ReusableEnrollmentTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken) is { } reusable)
        {
            return await EnrollWithReusableTokenAsync(request, reusable, keyStorage, panelOrigin, now, cancellationToken);
        }

        if (token is null || !token.IsUsable(now))
        {
            return await RejectEnrollmentAsync(token?.NodeId, token is null ? "unknown_token" : "token_expired_or_used", cancellationToken);
        }

        var node = await db.Nodes.SingleAsync(n => n.Id == token.NodeId, cancellationToken);
        if (node.Status == NodeStatus.Disabled)
        {
            return await RejectEnrollmentAsync(node.Id, "node_disabled", cancellationToken);
        }

        var fingerprint = ProtocolCrypto.Fingerprint(request.PublicKey);
        var credential = NodeCredential.Enrolled(node.Id, request.PublicKey, fingerprint, keyStorage, now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // With the node's row locked, as a rotation locks it: a key the old one registers meanwhile is found below.
        await locks.LockNodeAsync(node.Id, cancellationToken);

        // Atomic consumption: two agents racing with the same token cannot both enroll.
        var consumed = await db.NodeEnrollmentTokens
            .Where(t => t.Id == token.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), cancellationToken);
        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await RejectEnrollmentAsync(node.Id, "token_already_used", cancellationToken);
        }

        // Re-enrollment (e.g. Windows reinstalled): the previous key stops working, and so does a pending one from an
        // unfinished rotation (it could still authenticate and supersede the new key).
        var previous = await db.NodeCredentials
            .Where(c => c.NodeId == node.Id && (c.Status == NodeCredentialStatus.Active || c.Status == NodeCredentialStatus.Pending))
            .ToListAsync(cancellationToken);
        foreach (var old in previous)
        {
            old.Revoke(null, "Replaced by a new enrollment.", now);
        }

        db.NodeCredentials.Add(credential);
        token.Consumed(credential.Id, now); // saved with the credential, which the reference needs to exist first
        var inventory = ToSnapshot(request.Inventory, request.AgentVersion, AgentProtocol.CurrentVersion);
        node.Enrolled(inventory, now);
        // What the agent pins from the answer: the panel signs its sessions with one of them before its first Hello says so.
        var panelKeys = await keys.GetPublicKeysAsync(cancellationToken);
        node.PanelKeysPinned(panelKeys.Select(k => k.KeyId));
        audit.Record(AuditActions.Node.Enrolled, AuditOutcome.Success, NodeActor(node),
                new { hostname = inventory.Hostname, fingerprint, keyStorage = keyStorage.ToString().ToLowerInvariant(), replacedCredentials = previous.Count })
            .WithNode(node.Id).WithTarget("node", node.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (previous.Count > 0)
        {
            sessions.EndAll(node.Id);
            connections.Disconnect(node.Id);
        }

        LogEnrolled(node.Id, inventory.Hostname, fingerprint);
        var result = new EnrollmentResult(node.Id.Value, credential.Id, panelOrigin, panelKeys);
        return await keys.SignAsync(AgentProtocol.Purposes.EnrollmentResult, ProtocolJson.Serialize(result), cancellationToken);
    }

    /// <summary>
    /// Enrollment with a reusable token (Phase 14): a new node named after the machine, never an existing one (a leaked
    /// token must not take a node over), waiting for approval when the token requires it.
    /// </summary>
    private async Task<Result<SignedMessage>> EnrollWithReusableTokenAsync(EnrollRequest request, ReusableEnrollmentToken reusable,
        NodeKeyStorage keyStorage, string panelOrigin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!reusable.IsUsable(now))
        {
            return await RejectTokenEnrollmentAsync(reusable, "reusable_token_unusable", cancellationToken);
        }

        var inventory = ToSnapshot(request.Inventory, request.AgentVersion, AgentProtocol.CurrentVersion);
        var created = Node.EnrolledWithToken(await FreeNodeNameAsync(inventory.Hostname, cancellationToken), inventory, reusable.Id, reusable.RequireApproval,
            now);
        if (!created.IsSuccess)
        {
            return await RejectTokenEnrollmentAsync(reusable, "node_name_invalid", cancellationToken);
        }

        var node = created.Value;
        var fingerprint = ProtocolCrypto.Fingerprint(request.PublicKey);
        var credential = NodeCredential.Enrolled(node.Id, request.PublicKey, fingerprint, keyStorage, now);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Atomic count: machines racing for the last use cannot all enroll.
        var counted = await db.ReusableEnrollmentTokens
            .Where(t => t.Id == reusable.Id && t.RevokedAt == null && t.ExpiresAt > now && t.Uses < t.MaxUses)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Uses, t => t.Uses + 1).SetProperty(t => t.LastUsedAt, now), cancellationToken);
        if (counted != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await RejectTokenEnrollmentAsync(reusable, "reusable_token_used_up", cancellationToken);
        }

        // What the agent pins from the answer: the panel signs its sessions with one of them before its first Hello says so.
        var panelKeys = await keys.GetPublicKeysAsync(cancellationToken);
        node.PanelKeysPinned(panelKeys.Select(k => k.KeyId));
        db.Nodes.Add(node);
        db.NodeCredentials.Add(credential);
        var actor = NodeActor(node);
        audit.Record(AuditActions.Node.Created, AuditOutcome.Success, actor, new { name = node.Name, enrollmentToken = reusable.Name })
            .WithNode(node.Id).WithTarget("node", node.Id.ToString());
        audit.Record(AuditActions.Node.Enrolled, AuditOutcome.Success, actor, new
        {
            hostname = inventory.Hostname,
            fingerprint,
            keyStorage = keyStorage.ToString().ToLowerInvariant(),
            enrollmentToken = reusable.Name,
            awaitingApproval = node.ApprovalRequired,
        }).WithNode(node.Id).WithTarget("node", node.Id.ToString());

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (ex.InnerException is DbException { SqlState: UniqueViolation })
            {
                // Another machine of the same name may have taken it meanwhile: the next free one (a failed save inside
                // the transaction is rolled back to its savepoint, so it can be saved again). Any other duplicate (the
                // key is registered already: a replayed request) refuses the enrollment, without using the token.
                var nameTaken = await db.Nodes.AnyAsync(n => n.NormalizedName == node.NormalizedName, cancellationToken);
                if (nameTaken && attempt < 3)
                {
                    node.Update($"{Truncate(node.Name, Node.MaxNameLength - 9)}-{node.Id.Value.ToString("N")[^8..]}", null, null, now);
                    continue;
                }

                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return await RejectTokenEnrollmentAsync(reusable, nameTaken ? "node_name_taken" : "key_already_registered", cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        LogEnrolledWithToken(node.Id, node.Name, inventory.Hostname, fingerprint, reusable.Name, node.ApprovalRequired);
        var result = new EnrollmentResult(node.Id.Value, credential.Id, panelOrigin, panelKeys, node.ApprovalRequired);
        return await keys.SignAsync(AgentProtocol.Purposes.EnrollmentResult, ProtocolJson.Serialize(result), cancellationToken);
    }

    /// <summary>The machine's name (cleaned by <see cref="ToSnapshot"/>), or with a number after it when a node has that name already.</summary>
    private async Task<string> FreeNodeNameAsync(string hostname, CancellationToken cancellationToken)
    {
        var name = Truncate(hostname.Trim(), Node.MaxNameLength - 4);
        name = name.Length == 0 ? "node" : name;
        for (var number = 1; number < 100; number++)
        {
            var candidate = number == 1 ? name : $"{name}-{number}";
            if (!await db.Nodes.AnyAsync(n => n.NormalizedName == Node.Normalize(candidate), cancellationToken))
            {
                return candidate;
            }
        }

        return $"{Truncate(name, Node.MaxNameLength - 9)}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    public async Task<Result<ChallengeResponse>> ChallengeAsync(ChallengeRequest request, string panelOrigin, AgentTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);
        await transportPolicy.EnsureLoadedAsync(db, cancellationToken);
        var nodeId = new NodeId(request.NodeId);
        if (request.Nonce is { Length: > MaxChallengeNonceLength })
        {
            LogAuthenticationRejected(nodeId, "challenge nonce too long");
            return await RefuseAsync(nodeId, "challenge_nonce_too_long", request.CredentialId, cancellationToken);
        }

        if (request.PinnedPanelKeys is not null && !AgentProtocol.ArePinnedPanelKeyIds(request.PinnedPanelKeys))
        {
            LogAuthenticationRejected(nodeId, "pinned panel key ids malformed");
            return await RefuseAsync(nodeId, "challenge_pinned_keys_invalid", request.CredentialId, cancellationToken);
        }

        // Pending credentials too: authenticating with one completes a rotation.
        var credential = await db.NodeCredentials.Where(c => c.Id == request.CredentialId && c.NodeId == nodeId
                && (c.Status == NodeCredentialStatus.Active || c.Status == NodeCredentialStatus.Pending)
                && db.Nodes.Any(n => n.Id == nodeId && n.Status == NodeStatus.Active))
            .Select(c => new { c.PublicKey, c.Status, c.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (credential is null)
        {
            LogAuthenticationRejected(nodeId, "challenge for unknown/revoked credential or inactive node");
            return await RefuseAsync(nodeId, "credential_or_node_inactive", request.CredentialId, cancellationToken);
        }

        if (credential.Status == NodeCredentialStatus.Pending && clock.GetUtcNow() >= credential.CreatedAt + NodeCredential.PendingLifetime)
        {
            return await RefuseExpiredPendingAsync(nodeId, request.CredentialId, cancellationToken);
        }

        var key = credential.PublicKey;
        if (await RefuseTransportAsync(nodeId, request.CredentialId, key, transport, cancellationToken) is { } refused)
        {
            return refused;
        }

        var challenge = challenges.Issue(nodeId, request.CredentialId, panelOrigin, request.Nonce, request.PinnedPanelKeys);
        return new ChallengeResponse(challenge.ChallengeId, challenge.Nonce, challenge.PanelOrigin);
    }

    public async Task<Result<TokenResponse>> AuthenticateAsync(TokenRequest request, string panelOrigin, AgentTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transport);
        await transportPolicy.EnsureLoadedAsync(db, cancellationToken);
        var challenge = challenges.Consume(request.ChallengeId);
        if (challenge is null)
        {
            LogAuthenticationRejected(null, "unknown, expired or reused challenge");
            return AgentErrors.AuthenticationRejected;
        }

        if (!string.Equals(challenge.PanelOrigin, panelOrigin, StringComparison.Ordinal))
        {
            LogAuthenticationRejected(challenge.NodeId, "challenge issued for another panel address");
            return await RefuseAsync(challenge.NodeId, "challenge_origin_mismatch", challenge.CredentialId, cancellationToken);
        }

        // Re-checked: the credential may have been revoked or the node disabled since the challenge.
        var credential = await db.NodeCredentials.SingleOrDefaultAsync(c => c.Id == challenge.CredentialId && c.NodeId == challenge.NodeId, cancellationToken);
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == challenge.NodeId, cancellationToken);
        if (credential is not { CanAuthenticate: true } || node is not { AcceptsConnections: true })
        {
            LogAuthenticationRejected(challenge.NodeId, "credential revoked or node inactive after the challenge");
            return await RefuseAsync(challenge.NodeId, "inactive_after_challenge", challenge.CredentialId, cancellationToken);
        }

        if (credential.PendingExpired(clock.GetUtcNow()))
        {
            return await RefuseExpiredPendingAsync(node.Id, credential.Id, cancellationToken);
        }

        // Again: the token may come over another TLS connection than the challenge did.
        if (await RefuseTransportAsync(node.Id, credential.Id, credential.PublicKey, transport, cancellationToken) is { } refused)
        {
            return refused;
        }

        // The keys the challenge request named are signed too: the node's record of them comes from here only.
        var signed = ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(challenge.NodeId.Value), ProtocolCrypto.GuidBytes(challenge.CredentialId),
            challenge.Nonce, ProtocolCrypto.Utf8(challenge.PanelOrigin), ProtocolCrypto.KeyIds(challenge.PinnedPanelKeys));
        if (request.Signature is null || !ProtocolCrypto.Verify(credential.PublicKey, signed, request.Signature))
        {
            LogAuthenticationRejected(challenge.NodeId, "invalid signature");
            return await RefuseAsync(node.Id, "invalid_signature", credential.Id, cancellationToken);
        }

        if (credential.Status == NodeCredentialStatus.Pending)
        {
            if (await CompleteRotationAsync(node, credential, challenge.IssuedAt, SignedInAt(challenge), challenge.PinnedPanelKeys, cancellationToken) is { } reason)
            {
                LogAuthenticationRejected(node.Id, reason == CurrentSignedInAfterChallenge
                    ? "the current credential signed in after the pending credential's challenge was issued"
                    : "pending credential revoked or node inactive before the rotation completed");
                return await RefuseAsync(node.Id, reason, credential.Id, cancellationToken);
            }
        }
        else
        {
            // The node's row first, as a revocation and a rotation lock it before writing its keys: the save writes the
            // key's row before the node's, and the other order against them is a deadlock.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await locks.LockNodeAsync(node.Id, cancellationToken);

            // The time is taken with the node's row locked, where a pending key's sign-in compares it with its challenge.
            var now = SignedInAt(challenge);
            credential.Used(now);
            node.Seen(now);
            RecordPinnedKeys(node, challenge.PinnedPanelKeys);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < 2)
                {
                    // The node row changed meanwhile (its agent reported in): mark it seen, with the keys it signed, on the
                    // fresh rows, unless the credential was revoked in between.
                    await db.Entry(node).ReloadAsync(cancellationToken);
                    await db.Entry(credential).ReloadAsync(cancellationToken);
                    if (!credential.CanAuthenticate || !node.AcceptsConnections)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        LogAuthenticationRejected(node.Id, "credential revoked or node inactive while signing in");
                        return await RefuseAsync(node.Id, "inactive_while_signing_in", credential.Id, cancellationToken);
                    }

                    credential.Used(now);
                    node.Seen(now);
                    if (challenge.PinnedPanelKeys is { } pinned)
                    {
                        node.PanelKeysPinned(pinned);
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        sessions.Prune();
        var (token, session) = sessions.Create(node.Id, credential.Id, transport.ThroughAgentsEndpoint ? transport.ClientKeyFingerprint : null);

        // The node disabled or deleted, or the key revoked, between the checks above and the session: whoever did it
        // ended the node's sessions before this one existed. Read once more now that it exists (they cut it off after
        // committing, so either this read sees their change or their cut-off sees this session).
        var stillAllowed = await db.NodeCredentials.AsNoTracking().AnyAsync(c => c.Id == credential.Id && c.NodeId == node.Id
            && c.Status == NodeCredentialStatus.Active
            && db.Nodes.Any(n => n.Id == node.Id && n.Status == NodeStatus.Active), cancellationToken);
        if (!stillAllowed)
        {
            sessions.End(session.SessionId);
            LogAuthenticationRejected(node.Id, "credential revoked or node inactive while signing in");
            return await RefuseAsync(node.Id, "inactive_while_signing_in", credential.Id, cancellationToken);
        }

        // Certificates became required while this sign-in ran: the switch ended the sessions without one before this
        // one existed. A full fence between creating the session and reading the switch; the switch has the other one
        // (AgentTransportPolicy.Set) between changing and ending the sessions.
        Interlocked.MemoryBarrier();
        if (session.CertificateKey is null && transportPolicy.RequiresCertificateFor(transport))
        {
            sessions.End(session.SessionId);
            return AgentErrors.CertificateRequired;
        }

        return new TokenResponse(token, session.SessionId, session.ExpiresAt, await GrantAsync(node, session, challenge, cancellationToken));
    }

    /// <summary>
    /// The session, signed for the nonce the agent sent with its challenge request (docs/architecture/03, "Authentication")
    /// with a panel key this node pins: proof for the agent that this panel recorded the sign-in, which whatever answers it
    /// on the way cannot forge, and which the agent asks for before it gives up a pending key ("Rotation"). None without
    /// a nonce (an older agent) or a pinned key (an agent that missed a whole panel key rotation).
    /// </summary>
    private async Task<SignedMessage?> GrantAsync(Node node, AgentSession session, AgentChallengeStore.PendingChallenge challenge,
        CancellationToken cancellationToken)
    {
        if (challenge.AgentNonce is not { } nonce)
        {
            return null;
        }

        // The keys the agent named in this sign-in's challenge request (and signed), never the node's stored pins: those
        // are an earlier sign-in's.
        var grant = new SessionGrant(node.Id.Value, session.CredentialId, session.SessionId, session.ExpiresAt, nonce, clock.GetUtcNow());
        var signed = await keys.SignForAsync(AgentProtocol.Purposes.SessionGrant, ProtocolJson.Serialize(grant), challenge.PinnedPanelKeys, cancellationToken);
        if (signed is null)
        {
            LogNoGrant(node.Id);
        }

        return signed;
    }

    /// <summary>
    /// The time a sign-in records for its key: now, but never before its own challenge was issued, which is after every
    /// challenge issued earlier whatever the clock did (<see cref="AgentChallengeStore.PendingChallenge.IssuedAt"/>). A
    /// pending key's sign-in held back since a challenge issued before this one is then refused
    /// (<see cref="CompleteRotationAsync"/>), also after the clock stepped back.
    /// </summary>
    private DateTimeOffset SignedInAt(AgentChallengeStore.PendingChallenge challenge)
    {
        var now = clock.GetUtcNow();
        return now > challenge.IssuedAt ? now : challenge.IssuedAt;
    }

    /// <summary>
    /// The address of the agents' endpoint for one node (docs/architecture/03, "Client certificates"), signed with the
    /// panel key, as a refusal with <see cref="AgentErrors.CertificateRequired"/> carries it.
    /// </summary>
    public Task<SignedMessage> SignAgentsAddressAsync(Guid nodeId, AgentsEndpointAddress agents, byte[]? nonce, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return keys.SignAsync(AgentProtocol.Purposes.AgentsAddress, ProtocolJson.Serialize(new AgentsAddress(nodeId, agents.Port, agents.Url, nonce)),
            cancellationToken);
    }

    /// <summary>
    /// Through the agents' endpoint the client certificate must be the credential's key. Elsewhere, an agent is sent
    /// there while certificates are required: not a failure (the Nodes page lists the nodes that have not moved), so it
    /// is not audited.
    /// </summary>
    private async Task<DomainError?> RefuseTransportAsync(NodeId node, Guid credentialId, byte[] credentialKey, AgentTransport transport,
        CancellationToken cancellationToken)
    {
        if (transport.ThroughAgentsEndpoint)
        {
            if (transport.PresentsKey(credentialKey))
            {
                return null;
            }

            LogAuthenticationRejected(node, "the client certificate is missing or is not the node key");
            return await RefuseAsync(node, "client_certificate_mismatch", credentialId, cancellationToken);
        }

        if (transportPolicy.RequiresCertificateFor(transport))
        {
            LogSentToAgentsAddress(node);
            return AgentErrors.CertificateRequired;
        }

        return null;
    }

    /// <summary>
    /// A request with a session token that does not come with the session's client certificate (docs/architecture/03,
    /// "Client certificates"): the token was copied, or is played back somewhere else. Audited as a refused sign-in.
    /// </summary>
    public async Task SessionRefusedAsync(NodeId node, Guid credential, CancellationToken cancellationToken) =>
        await RefuseAsync(node, "session_certificate_mismatch", credential, cancellationToken);

    /// <summary>
    /// The agent's answer to a refused sign-in or rotation (<paramref name="answer"/>, the generic one by default),
    /// audited when the request names an existing node: at most one entry per node, reason and address a while
    /// (<see cref="AgentRefusalBudget"/>), so an agent that keeps trying cannot fill the log.
    /// </summary>
    private async Task<DomainError> RefuseAsync(NodeId node, string reason, Guid credential, CancellationToken cancellationToken,
        string action = AuditActions.Node.AuthenticationFailed, DomainError? answer = null)
    {
        var name = await db.Nodes.AsNoTracking().Where(n => n.Id == node).Select(n => n.Name).SingleOrDefaultAsync(cancellationToken);
        if (name is not null && refusals.TryRecord(node, reason, requestContext.RequestInfo.IpAddress, out var leftOut))
        {
            audit.Record(action, AuditOutcome.Failure, new AuditActor(AuditActorType.Node, null, name),
                    leftOut == 0 ? new { reason, credential } : new { reason, credential, notRecordedBefore = leftOut })
                .WithNode(node).WithTarget("node", node.ToString());
            await db.SaveChangesAsync(cancellationToken);
        }

        return answer ?? AgentErrors.AuthenticationRejected;
    }

    /// <summary>
    /// Registers the node's next key as a pending credential (docs/architecture/03, "Rotation"). The caller is the
    /// agent's current session, and the current key signs the new one for this session (the session's token is a bearer
    /// token, which can be copied: it never registers a key alone); the proof proves the new one. The node keeps using
    /// its current credential until it authenticates with the new one. The node's row is locked meanwhile: a revocation
    /// of the current key (which also revokes pending ones) and this registration take turns, so none is left behind.
    /// </summary>
    public async Task<Result<SignedMessage>> RotateCredentialAsync(
        NodeId nodeId, Guid credentialId, Guid sessionId, RotateCredentialRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockNodeAsync(nodeId, cancellationToken);
        var session = sessions.Find(sessionId);
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == nodeId, cancellationToken);
        var current = await db.NodeCredentials.SingleOrDefaultAsync(c => c.Id == credentialId && c.NodeId == nodeId, cancellationToken);
        if (session is null || session.NodeId != nodeId || session.CredentialId != credentialId
            || node is not { AcceptsConnections: true } || current is not { IsActive: true })
        {
            await transaction.RollbackAsync(cancellationToken);
            LogAuthenticationRejected(nodeId, "rotation from an ended session, inactive node or inactive credential");
            return await RefuseAsync(nodeId, "rotation_without_active_session", credentialId, cancellationToken);
        }

        if (request.PublicKey is null || request.CurrentKeyProof is null
            || !ProtocolCrypto.Verify(current.PublicKey, RotationData(AgentProtocol.Purposes.AuthorizeRotation, nodeId, credentialId, sessionId, request.PublicKey),
                request.CurrentKeyProof))
        {
            // The session's token without its key: copied, or an agent older than this check.
            await transaction.RollbackAsync(cancellationToken);
            LogRotationRejected(nodeId, "the current key did not sign the new key for this session");
            return await RefuseAsync(nodeId, "invalid_current_key_proof", credentialId, cancellationToken, AuditActions.Node.CredentialRotationStarted,
                AgentErrors.RotationRejected);
        }

        if (!TryParseKeyStorage(request.KeyStorage, out var keyStorage) || request.Proof is null
            || !VerifyRotationProof(nodeId, credentialId, sessionId, request))
        {
            await transaction.RollbackAsync(cancellationToken);
            LogRotationRejected(nodeId, "malformed request or invalid proof of the new key");
            return await RefuseAsync(nodeId, "invalid_proof", credentialId, cancellationToken, AuditActions.Node.CredentialRotationStarted,
                AgentErrors.RotationRejected);
        }

        var fingerprint = ProtocolCrypto.Fingerprint(request.PublicKey);
        if (await db.NodeCredentials.AnyAsync(c => c.Fingerprint == fingerprint, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            LogRotationRejected(nodeId, "the new key is already registered");
            return await RefuseAsync(nodeId, "key_already_registered", credentialId, cancellationToken, AuditActions.Node.CredentialRotationStarted,
                AgentErrors.RotationRejected);
        }

        // Only the latest rotation can complete: an earlier pending key (the agent lost it) is dropped.
        var now = clock.GetUtcNow();
        var stale = await db.NodeCredentials.Where(c => c.NodeId == nodeId && c.Status == NodeCredentialStatus.Pending).ToListAsync(cancellationToken);
        foreach (var pending in stale)
        {
            pending.Supersede(now);
        }

        var credential = NodeCredential.Pending(nodeId, request.PublicKey, fingerprint, keyStorage, now);
        db.NodeCredentials.Add(credential);
        audit.Record(AuditActions.Node.CredentialRotationStarted, AuditOutcome.Success, NodeActor(node),
                new { fingerprint, keyStorage = keyStorage.ToString().ToLowerInvariant(), replaces = current.Fingerprint })
            .WithNode(nodeId).WithTarget("node", nodeId.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var result = new CredentialRotation(nodeId.Value, credential.Id, fingerprint);
        return await keys.SignAsync(AgentProtocol.Purposes.RotationResult, ProtocolJson.Serialize(result), cancellationToken);
    }

    /// <summary>
    /// A pending credential past <see cref="NodeCredential.PendingLifetime"/>: superseded (unless something else changed
    /// it meanwhile), and the sign-in refused.
    /// </summary>
    private async Task<DomainError> RefuseExpiredPendingAsync(NodeId node, Guid credentialId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // The node's row first, as every writer of its keys takes it.
            await locks.LockNodeAsync(node, cancellationToken);
            await db.NodeCredentials.Where(c => c.Id == credentialId && c.NodeId == node && c.Status == NodeCredentialStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, NodeCredentialStatus.Superseded).SetProperty(c => c.SupersededAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        LogAuthenticationRejected(node, "pending credential not used within its lifetime");
        return await RefuseAsync(node, "pending_credential_expired", credentialId, cancellationToken);
    }

    /// <summary>The refusal of a pending credential's sign-in whose challenge is older than the current credential's last sign-in.</summary>
    private const string CurrentSignedInAfterChallenge = "current_credential_signed_in_after_challenge";

    /// <summary>
    /// The first authentication with a pending credential: it becomes active and the previous one is superseded. The
    /// reason of the refusal (null when it completed) when it was revoked (a revocation of the current key revokes it
    /// too), it expired, or the node was disabled since it was read; or when the current credential signed in after
    /// this sign-in's challenge was issued (<paramref name="challengeIssuedAt"/>): the agent signs in with its current
    /// key only when the pending one was refused, and deletes the pending one once that works, so a sign-in with it that
    /// arrives later was held back (a proxy that forged the refusal) and would activate a key the agent no longer has.
    /// </summary>
    private async Task<string?> CompleteRotationAsync(Node node, NodeCredential credential, DateTimeOffset challengeIssuedAt, DateTimeOffset now,
        IReadOnlyList<string>? pinned, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // With the node's row locked, as a revocation locks it: read both again, so a revocation that committed meanwhile
        // is not overwritten by the activation.
        await locks.LockNodeAsync(node.Id, cancellationToken);
        await db.Entry(node).ReloadAsync(cancellationToken);
        await db.Entry(credential).ReloadAsync(cancellationToken);
        if (db.Entry(node).State == EntityState.Detached || db.Entry(credential).State == EntityState.Detached
            || credential.Status != NodeCredentialStatus.Pending || credential.PendingExpired(now) || !node.AcceptsConnections)
        {
            await transaction.RollbackAsync(cancellationToken);
            return "inactive_while_signing_in";
        }

        // The current credential's sign-in records its time with this row locked too: either it committed before (and is
        // seen here) or it reads the current credential superseded once this commits, and is refused.
        var previous = await db.NodeCredentials.Where(c => c.NodeId == node.Id && c.Status == NodeCredentialStatus.Active).ToListAsync(cancellationToken);
        if (previous.Any(c => c.LastUsedAt > challengeIssuedAt))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CurrentSignedInAfterChallenge;
        }

        credential.Used(now);
        node.Seen(now);
        RecordPinnedKeys(node, pinned);
        foreach (var old in previous)
        {
            old.Supersede(now);
        }

        // Two saves: "at most one active credential" is a unique index, checked per statement.
        await db.SaveChangesAsync(cancellationToken);
        credential.Activate(now);
        audit.Record(AuditActions.Node.CredentialRotated, AuditOutcome.Success, NodeActor(node),
                new { fingerprint = credential.Fingerprint, replaced = previous.Select(p => p.Fingerprint).ToList() })
            .WithNode(node.Id).WithTarget("node", node.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Sessions of the old key end now, and the connections they opened close: the hub's would otherwise keep reporting
        // until the agent's new connection says Hello (an agent that just signed in with its new key opens one at once),
        // and those on the agents' endpoint (whoever else holds the old key, with no hub connection at all) would hold the
        // node's share of it until their session's end. The new key's connections stay.
        foreach (var old in previous)
        {
            sessions.EndCredential(old.Id);
        }

        if (previous.Count > 0)
        {
            connections.DisconnectEnded(node.Id);
        }

        LogRotated(node.Id, credential.Fingerprint);
        return null;
    }

    /// <summary>
    /// Records the panel keys a sign-in's challenge request named, which the node key signed with it: after enrollment,
    /// the only source of the node's record (docs/architecture/03, "Panel key rotation"). What a connection says (its
    /// Hello, its answer to new keys) is not signed by the node key, and whoever holds a session token can say it.
    /// </summary>
    private void RecordPinnedKeys(Node node, IReadOnlyList<string>? pinned)
    {
        if (pinned is null || (node.PinnedPanelKeys ?? []).SequenceEqual(pinned.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return;
        }

        audit.Record(AuditActions.Node.PanelKeysPinned, AuditOutcome.Success, NodeActor(node), new { before = node.PinnedPanelKeys, after = pinned })
            .WithNode(node.Id).WithTarget("node", node.Id.ToString());
        node.PanelKeysPinned(pinned);
    }

    private static bool VerifyRotationProof(NodeId nodeId, Guid credentialId, Guid sessionId, RotateCredentialRequest request)
    {
        try
        {
            using var key = ProtocolCrypto.ImportPublicKey(request.PublicKey);
            return ProtocolCrypto.Verify(key, RotationData(AgentProtocol.Purposes.RotateCredential, nodeId, credentialId, sessionId, request.PublicKey), request.Proof);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>What both keys sign for a rotation, each with its purpose: this node, its current key, this session and the new key.</summary>
    private static byte[] RotationData(string purpose, NodeId nodeId, Guid credentialId, Guid sessionId, byte[] publicKey) =>
        ProtocolCrypto.SignedData(purpose, ProtocolCrypto.GuidBytes(nodeId.Value), ProtocolCrypto.GuidBytes(credentialId), ProtocolCrypto.GuidBytes(sessionId),
            publicKey);

    private static bool TryParseKeyStorage(string? value, out NodeKeyStorage keyStorage) =>
        Enum.TryParse(value, ignoreCase: true, out keyStorage) && Enum.IsDefined(keyStorage);

    /// <summary>
    /// What the panel keeps of the agent's inventory (<see cref="AgentInput.Inventory"/>), at enrollment and at every
    /// Hello. The host name is shown as the machine's identity and names the nodes of reusable tokens; like every text
    /// in it, it loses the characters that would hide in it (format characters such as zero-width spaces and direction
    /// overrides, line breaks) and is cut to its column.
    /// </summary>
    public static NodeInventorySnapshot ToSnapshot(NodeInventory inventory, string agentVersion, int protocolVersion) =>
        AgentInput.Inventory(inventory, agentVersion, protocolVersion);

    public static AuditActor NodeActor(Node node) => new(AuditActorType.Node, null, node.Name);

    private static bool IsWellFormed(EnrollRequest request, out NodeKeyStorage keyStorage, out string reason)
    {
        keyStorage = NodeKeyStorage.Software;
        if (string.IsNullOrEmpty(request.Token) || request.Token.Length > MaxTokenLength)
        {
            reason = "malformed_token";
            return false;
        }

        if (request.Inventory is null || string.IsNullOrWhiteSpace(request.Inventory.Hostname) || string.IsNullOrWhiteSpace(request.AgentVersion))
        {
            reason = "missing_inventory";
            return false;
        }

        if (!TryParseKeyStorage(request.KeyStorage, out keyStorage))
        {
            reason = "unknown_key_storage";
            return false;
        }

        if (request.PublicKey is null || request.Proof is null)
        {
            reason = "missing_key";
            return false;
        }

        try
        {
            using var key = ProtocolCrypto.ImportPublicKey(request.PublicKey);
            var signed = ProtocolCrypto.SignedData(AgentProtocol.Purposes.Enroll, ProtocolCrypto.Utf8(request.Token), request.PublicKey);
            if (!ProtocolCrypto.Verify(key, signed, request.Proof))
            {
                reason = "invalid_proof";
                return false;
            }
        }
        catch (CryptographicException)
        {
            reason = "invalid_key";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// A refused enrollment. One that names a node (its token is real, used or expired) is always audited; the others
    /// (an unknown token, a malformed request: anyone can send them) within <see cref="EnrollmentRefusalBudget"/>, the
    /// next entry saying how many were left out.
    /// </summary>
    private async Task<Result<SignedMessage>> RejectEnrollmentAsync(NodeId? node, string reason, CancellationToken cancellationToken)
    {
        LogEnrollmentRejected(reason);
        var leftOut = 0;
        if (node is null && !enrollmentRefusals.TryRecord(out leftOut))
        {
            return AgentErrors.EnrollmentRejected;
        }

        var entry = audit.Record(AuditActions.Node.EnrollmentFailed, AuditOutcome.Failure,
            metadata: leftOut == 0 ? new { reason } : new { reason, notRecordedBefore = leftOut });
        if (node is { } id)
        {
            entry.WithNode(id).WithTarget("node", id.ToString());
        }

        await db.SaveChangesAsync(cancellationToken);
        return AgentErrors.EnrollmentRejected;
    }

    /// <summary>A refused use of a reusable token: the audit says which token, so a leaked one can be found and revoked.</summary>
    private async Task<Result<SignedMessage>> RejectTokenEnrollmentAsync(ReusableEnrollmentToken token, string reason, CancellationToken cancellationToken)
    {
        audit.Record(AuditActions.Node.EnrollmentFailed, AuditOutcome.Failure, metadata: new { reason, enrollmentToken = token.Name, enrollmentTokenId = token.Id })
            .WithTarget("enrollment_token", token.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        LogEnrollmentRejected(reason);
        return AgentErrors.EnrollmentRejected;
    }

    /// <summary>At most <paramref name="max"/> characters, never half of a surrogate pair (PostgreSQL refuses it).</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} enrolled from {Hostname} with key {Fingerprint}")]
    private partial void LogEnrolled(NodeId nodeId, string hostname, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Node {NodeId} ({Name}) created and enrolled from {Hostname} with key {Fingerprint} by the reusable token {Token} (waiting for approval: {AwaitingApproval})")]
    private partial void LogEnrolledWithToken(NodeId nodeId, string name, string hostname, string fingerprint, string token, bool awaitingApproval);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment rejected: {Reason}")]
    private partial void LogEnrollmentRejected(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent authentication rejected for node {NodeId}: {Reason}")]
    private partial void LogAuthenticationRejected(NodeId? nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} signed in without a client certificate, which this panel requires: sent to the agents' address")]
    private partial void LogSentToAgentsAddress(NodeId nodeId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Node {NodeId} signed in pinning none of the panel's keys: its session has no grant, which its agent refuses (it missed a whole panel key rotation; enroll it again)")]
    private partial void LogNoGrant(NodeId nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential rotation rejected for node {NodeId}: {Reason}")]
    private partial void LogRotationRejected(NodeId nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} rotated its key; now using {Fingerprint}")]
    private partial void LogRotated(NodeId nodeId, string fingerprint);
}
