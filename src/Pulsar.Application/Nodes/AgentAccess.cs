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
    TimeProvider clock,
    ILogger<AgentAccess> logger)
{
    private const int MaxTokenLength = 128;

    public async Task<Result<SignedMessage>> EnrollAsync(EnrollRequest request, string panelOrigin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsWellFormed(request, out var keyStorage, out var reason))
        {
            return await RejectEnrollmentAsync(null, reason, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var hash = NodeEnrollmentToken.Hash(request.Token);
        var token = await db.NodeEnrollmentTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
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

        // Atomic consumption: two agents racing with the same token cannot both enroll.
        var consumed = await db.NodeEnrollmentTokens
            .Where(t => t.Id == token.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), cancellationToken);
        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await RejectEnrollmentAsync(node.Id, "token_already_used", cancellationToken);
        }

        // Re-enrollment (e.g. Windows reinstalled): the previous key stops working.
        var previous = await db.NodeCredentials.Where(c => c.NodeId == node.Id && c.Status == NodeCredentialStatus.Active).ToListAsync(cancellationToken);
        foreach (var old in previous)
        {
            old.Revoke(null, "Replaced by a new enrollment.", now);
        }

        db.NodeCredentials.Add(credential);
        token.Consumed(credential.Id, now); // saved with the credential, which the reference needs to exist first
        node.Enrolled(ToSnapshot(request.Inventory, request.AgentVersion, AgentProtocol.CurrentVersion), now);
        audit.Record(AuditActions.Node.Enrolled, AuditOutcome.Success, NodeActor(node),
                new { hostname = request.Inventory.Hostname, fingerprint, keyStorage = keyStorage.ToString().ToLowerInvariant(), replacedCredentials = previous.Count })
            .WithNode(node.Id).WithTarget("node", node.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (previous.Count > 0)
        {
            sessions.EndAll(node.Id);
            connections.Disconnect(node.Id);
        }

        LogEnrolled(node.Id, request.Inventory.Hostname, fingerprint);
        var result = new EnrollmentResult(node.Id.Value, credential.Id, panelOrigin, await keys.GetPublicKeysAsync(cancellationToken));
        return await keys.SignAsync(AgentProtocol.Purposes.EnrollmentResult, ProtocolJson.Serialize(result), cancellationToken);
    }

    public async Task<Result<ChallengeResponse>> ChallengeAsync(ChallengeRequest request, string panelOrigin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nodeId = new NodeId(request.NodeId);
        var allowed = await db.NodeCredentials.AnyAsync(c => c.Id == request.CredentialId && c.NodeId == nodeId
            && c.Status == NodeCredentialStatus.Active
            && db.Nodes.Any(n => n.Id == nodeId && n.Status == NodeStatus.Active), cancellationToken);
        if (!allowed)
        {
            LogAuthenticationRejected(nodeId, "challenge for unknown/revoked credential or inactive node");
            return AgentErrors.AuthenticationRejected;
        }

        var challenge = challenges.Issue(nodeId, request.CredentialId, panelOrigin);
        return new ChallengeResponse(challenge.ChallengeId, challenge.Nonce, challenge.PanelOrigin);
    }

    public async Task<Result<TokenResponse>> AuthenticateAsync(TokenRequest request, string panelOrigin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var challenge = challenges.Consume(request.ChallengeId);
        if (challenge is null || !string.Equals(challenge.PanelOrigin, panelOrigin, StringComparison.Ordinal))
        {
            LogAuthenticationRejected(challenge?.NodeId, "unknown, expired or reused challenge");
            return AgentErrors.AuthenticationRejected;
        }

        // Re-checked: the credential may have been revoked or the node disabled since the challenge.
        var credential = await db.NodeCredentials.SingleOrDefaultAsync(c => c.Id == challenge.CredentialId && c.NodeId == challenge.NodeId, cancellationToken);
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == challenge.NodeId, cancellationToken);
        if (credential is not { IsActive: true } || node is not { AcceptsConnections: true })
        {
            LogAuthenticationRejected(challenge.NodeId, "credential revoked or node inactive after the challenge");
            return AgentErrors.AuthenticationRejected;
        }

        var signed = ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(challenge.NodeId.Value), ProtocolCrypto.GuidBytes(challenge.CredentialId),
            challenge.Nonce, ProtocolCrypto.Utf8(challenge.PanelOrigin));
        if (request.Signature is null || !ProtocolCrypto.Verify(credential.PublicKey, signed, request.Signature))
        {
            audit.Record(AuditActions.Node.AuthenticationFailed, AuditOutcome.Failure, NodeActor(node), new { reason = "invalid_signature", credential = credential.Id })
                .WithNode(node.Id).WithTarget("node", node.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            LogAuthenticationRejected(challenge.NodeId, "invalid signature");
            return AgentErrors.AuthenticationRejected;
        }

        var now = clock.GetUtcNow();
        credential.Used(now);
        node.Seen(now);
        await db.SaveChangesAsync(cancellationToken);

        sessions.Prune();
        var (token, session) = sessions.Create(node.Id, credential.Id);
        return new TokenResponse(token, session.SessionId, session.ExpiresAt);
    }

    public static NodeInventorySnapshot ToSnapshot(NodeInventory inventory, string agentVersion, int protocolVersion) => new(
        Truncate(inventory.Hostname, 255),
        Truncate(inventory.OsDescription, 255),
        Truncate(inventory.OsBuild, 64),
        inventory.CpuModel is null ? null : Truncate(inventory.CpuModel, 255),
        inventory.LogicalCores,
        inventory.MemoryTotalBytes,
        inventory.ServersRoot is null ? null : Truncate(inventory.ServersRoot, 1024),
        Truncate(agentVersion, 32),
        protocolVersion);

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

        if (!Enum.TryParse(request.KeyStorage, ignoreCase: true, out keyStorage) || !Enum.IsDefined(keyStorage))
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

    private async Task<Result<SignedMessage>> RejectEnrollmentAsync(NodeId? node, string reason, CancellationToken cancellationToken)
    {
        var entry = audit.Record(AuditActions.Node.EnrollmentFailed, AuditOutcome.Failure, metadata: new { reason });
        if (node is { } id)
        {
            entry.WithNode(id).WithTarget("node", id.ToString());
        }

        await db.SaveChangesAsync(cancellationToken);
        LogEnrollmentRejected(reason);
        return AgentErrors.EnrollmentRejected;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} enrolled from {Hostname} with key {Fingerprint}")]
    private partial void LogEnrolled(NodeId nodeId, string hostname, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment rejected: {Reason}")]
    private partial void LogEnrollmentRejected(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent authentication rejected for node {NodeId}: {Reason}")]
    private partial void LogAuthenticationRejected(NodeId? nodeId, string reason);
}
