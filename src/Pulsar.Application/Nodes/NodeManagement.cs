using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Metrics;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

public sealed record NodeInventoryView(
    string Hostname,
    string OsDescription,
    string OsBuild,
    string? CpuModel,
    int LogicalCores,
    long MemoryTotalBytes,
    string? ServersRoot,
    string AgentVersion,
    int ProtocolVersion);

/// <param name="Uses">"servers", "backups".</param>
public sealed record DiskUsageView(string Root, long TotalBytes, long FreeBytes, IReadOnlyList<string> Uses);

/// <param name="Disks">The drives of the node's servers and backups folders; null from agents older than Phase 10.</param>
public sealed record NodeMetricsView(DateTimeOffset At, double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, TimeSpan Uptime,
    IReadOnlyList<DiskUsageView>? Disks)
{
    public static NodeMetricsView From(NodeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new NodeMetricsView(metrics.At, MetricHistory.CpuPercent(metrics.CpuPercent), metrics.MemoryUsedBytes, metrics.MemoryTotalBytes, metrics.Uptime,
            metrics.Disks?.Select(d => new DiskUsageView(d.Root, d.TotalBytes, d.FreeBytes, d.Uses)).ToList());
    }
}

/// <param name="EnrollmentTokenName">The reusable enrollment token that created the node, when one did (and still exists).</param>
/// <param name="CredentialFingerprint">The fingerprint of the node's active key, to check a machine before approving it.</param>
/// <param name="ConnectedWithCertificate">
/// Whether the agent's last connection presented its node key as a TLS client certificate (Phase 16); null before it
/// connected to a panel that records it.
/// </param>
/// <param name="AgentCanUseCertificate">Whether its agent can present one and move to the agents' address; null: unknown.</param>
public sealed record NodeSummary(
    Guid Id,
    string Name,
    string? Description,
    string? PublicHost,
    NodeStatus Status,
    bool Connected,
    DateTimeOffset? LastSeenAt,
    NodeInventoryView? Inventory,
    NodeMetricsView? Metrics,
    DateTimeOffset CreatedAt,
    PanelKeyTrust PanelKeyTrust,
    string? EnrollmentTokenName,
    string? CredentialFingerprint,
    bool? ConnectedWithCertificate,
    bool? AgentCanUseCertificate);

/// <summary>Whether the node's agent trusts the panel's active signing key (it may still pin only a retiring one).</summary>
public enum PanelKeyTrust
{
    /// <summary>The agent has not reported its pinned keys (never connected, or an agent without key rotation).</summary>
    Unknown,
    Current,
    /// <summary>It pins only retiring keys: retiring them would cut it off until it connects and takes the new key.</summary>
    Outdated,
}

public sealed record NodeCredentialView(
    Guid Id,
    string Fingerprint,
    NodeKeyStorage KeyStorage,
    NodeCredentialStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason,
    DateTimeOffset? SupersededAt);

public sealed record NodeDetails(NodeSummary Node, IReadOnlyList<NodeCredentialView> Credentials);

/// <summary>
/// Shown once to the administrator: the token and the fingerprint of the panel key, which the agent pins
/// (<c>PulsarAgent.exe enroll --panel … --token … --panel-key …</c>).
/// </summary>
public sealed record EnrollmentInstructions(string Token, DateTimeOffset ExpiresAt, string PanelKeyFingerprint);

public sealed record CreatedNode(NodeSummary Node, EnrollmentInstructions Enrollment);

public sealed record PingResult(AckStatus Status, double RoundTripMilliseconds);

/// <summary>The agent's answer to a command that starts work on the node (accepted means started, not finished).</summary>
public sealed record AgentCommandResult(AckStatus Status, string? ErrorCode);

/// <summary>Node administration from the UI (docs/architecture/03). Requires a signed-in actor.</summary>
public sealed class NodeManagement(
    IPulsarDbContext db,
    IPanelKeyRing keys,
    IAgentConnections connections,
    AgentSessionStore sessions,
    IRowLocks locks,
    IRequestContext request,
    IAuditLog audit,
    VisibleResources visible,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<NodeSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var nodes = await db.Nodes.AsNoTracking().OrderBy(n => n.NormalizedName).ToListAsync(cancellationToken);
        if (await visible.NodesAsync(cancellationToken) is { } sees)
        {
            nodes = [.. nodes.Where(n => sees(n.Id))];
        }

        return await SummarizeAsync(nodes, cancellationToken);
    }

    public async Task<Result<NodeDetails>> GetAsync(NodeId id, CancellationToken cancellationToken)
    {
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return NodeErrors.NotFound;
        }

        var credentials = await db.NodeCredentials.AsNoTracking()
            .Where(c => c.NodeId == id)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new NodeCredentialView(c.Id, c.Fingerprint, c.KeyStorage, c.Status, c.CreatedAt, c.LastUsedAt, c.RevokedAt, c.RevokeReason, c.SupersededAt))
            .ToListAsync(cancellationToken);
        return new NodeDetails(await SummarizeAsync(node, cancellationToken), credentials);
    }

    public async Task<Result<CreatedNode>> CreateAsync(string name, string? description, string? publicHost, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var created = Node.Create(name, description, publicHost, now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var node = created.Value;
        if (await db.Nodes.AnyAsync(n => n.NormalizedName == node.NormalizedName, cancellationToken))
        {
            return NodeErrors.NameTaken;
        }

        db.Nodes.Add(node);
        var instructions = await IssueTokenAsync(node, now, cancellationToken);
        audit.Record(AuditActions.Node.Created, metadata: new { name = node.Name }).WithNode(node.Id).WithTarget("node", node.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedNode(await SummarizeAsync(node, cancellationToken), instructions);
    }

    public async Task<Result<NodeSummary>> UpdateAsync(NodeId id, string name, string? description, string? publicHost, CancellationToken cancellationToken)
    {
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return NodeErrors.NotFound;
        }

        if (await db.Nodes.AnyAsync(n => n.NormalizedName == Node.Normalize(name ?? string.Empty) && n.Id != id, cancellationToken))
        {
            return NodeErrors.NameTaken;
        }

        var now = clock.GetUtcNow();
        var updated = node.Update(name ?? string.Empty, description, publicHost, now);
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        audit.Record(AuditActions.Node.Updated, metadata: new { name = node.Name }).WithNode(node.Id).WithTarget("node", node.Id.ToString());
        if (!await SaveNodeAsync(node, n => n.Update(name ?? string.Empty, description, publicHost, now), cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        return await SummarizeAsync(node, cancellationToken);
    }

    /// <summary>
    /// Lets the agents of nodes that waited for approval connect (they were enrolled with a reusable token that requires
    /// it, Phase 14). Nodes that need none are left as they are.
    /// </summary>
    public async Task<Result<IReadOnlyList<NodeSummary>>> ApproveAsync(IReadOnlyCollection<NodeId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct().ToList();
        if (wanted.Count is 0 or > MaxApprovals)
        {
            return NodeErrors.ApprovalBatchInvalid;
        }

        var nodes = await db.Nodes.Where(n => wanted.Contains(n.Id)).ToListAsync(cancellationToken);
        if (nodes.Count != wanted.Count)
        {
            return NodeErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        foreach (var node in nodes.Where(n => n.ApprovalRequired))
        {
            node.Approve(now);
            audit.Record(AuditActions.Node.Approved, metadata: new { name = node.Name, hostname = node.Inventory?.Hostname })
                .WithNode(node.Id).WithTarget("node", node.Id.ToString());
        }

        await db.SaveChangesAsync(cancellationToken);
        return await SummarizeAsync([.. nodes.OrderBy(n => n.NormalizedName)], cancellationToken);
    }

    /// <summary>The most nodes one approval takes.</summary>
    public const int MaxApprovals = 200;

    /// <summary>A new single-use token, for the first enrollment or to re-enroll a reinstalled machine.</summary>
    public async Task<Result<EnrollmentInstructions>> CreateEnrollmentTokenAsync(NodeId id, CancellationToken cancellationToken)
    {
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return NodeErrors.NotFound;
        }

        var instructions = await IssueTokenAsync(node, clock.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return instructions;
    }

    /// <summary>
    /// Revokes a node key and cuts the node off. Revoking the active key also revokes a key a rotation registered and
    /// that has not signed in yet: the active key's sessions registered it, so whoever holds the revoked key (a thief)
    /// may have. The node's row is locked, so a rotation registering a key meanwhile waits and then finds the key revoked.
    /// </summary>
    public async Task<Result> RevokeCredentialAsync(NodeId id, Guid credentialId, string? reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockNodeAsync(id, cancellationToken);
        var credential = await db.NodeCredentials.SingleOrDefaultAsync(c => c.Id == credentialId && c.NodeId == id, cancellationToken);
        if (credential is null)
        {
            return NodeErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var pending = credential.IsActive
            ? await db.NodeCredentials.Where(c => c.NodeId == id && c.Status == NodeCredentialStatus.Pending).ToListAsync(cancellationToken)
            : [];
        foreach (var key in pending.Prepend(credential))
        {
            key.Revoke(request.UserId, reason, now);
        }

        audit.Record(AuditActions.Node.CredentialRevoked, metadata: new
        {
            credential = credential.Id,
            fingerprint = credential.Fingerprint,
            reason,
            pendingRevoked = pending.Select(p => p.Fingerprint).ToList(),
        }).WithNode(id).WithTarget("node", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        CutOff(id);
        return Result.Success();
    }

    public Task<Result<NodeSummary>> DisableAsync(NodeId id, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, disable: true, cancellationToken);

    public Task<Result<NodeSummary>> EnableAsync(NodeId id, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, disable: false, cancellationToken);

    public async Task<Result> DeleteAsync(NodeId id, CancellationToken cancellationToken)
    {
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return NodeErrors.NotFound;
        }

        // Servers keep their files and accounts on the machine: they are deleted through their agent first.
        if (await db.Servers.AnyAsync(s => s.NodeId == id, cancellationToken))
        {
            return NodeErrors.HasServers;
        }

        db.Nodes.Remove(node);
        audit.Record(AuditActions.Node.Deleted, metadata: new { name = node.Name }).WithNode(id).WithTarget("node", id.ToString());
        if (!await SaveNodeAsync(node, n => db.Nodes.Remove(n), cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        CutOff(id);
        return Result.Success();
    }

    /// <summary>Round trip through the signed command path: panel signs, agent verifies and acks.</summary>
    public async Task<Result<PingResult>> PingAsync(NodeId id, CancellationToken cancellationToken)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == id, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        var started = Stopwatch.GetTimestamp();
        var ack = await connections.SendAsync(id, new PingCommand(null), request.UserId, cancellationToken);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        return new PingResult(ack.Status, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Asks the node's agent to replace its key now (it also does so on its own every 90 days).</summary>
    public async Task<Result<AgentCommandResult>> RotateCredentialAsync(NodeId id, CancellationToken cancellationToken)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == id, cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        var ack = await connections.SendAsync(id, new RotateCredentialCommand(), request.UserId, cancellationToken);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        audit.Record(AuditActions.Node.CredentialRotationRequested, ack.Status == AckStatus.Accepted ? AuditOutcome.Success : AuditOutcome.Failure,
                metadata: new { status = ack.Status.ToString(), ack.ErrorCode })
            .WithNode(id).WithTarget("node", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return new AgentCommandResult(ack.Status, ack.ErrorCode);
    }

    private async Task<Result<NodeSummary>> ChangeStatusAsync(NodeId id, bool disable, CancellationToken cancellationToken)
    {
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return NodeErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var changed = disable ? node.Disable(now) : node.Enable(now);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        audit.Record(disable ? AuditActions.Node.Disabled : AuditActions.Node.Enabled, metadata: new { name = node.Name })
            .WithNode(id).WithTarget("node", id.ToString());
        if (!await SaveNodeAsync(node, n => _ = disable ? n.Disable(now) : n.Enable(now), cancellationToken))
        {
            return NodeErrors.NotFound;
        }

        if (disable)
        {
            CutOff(id);
        }

        return await SummarizeAsync(node, cancellationToken);
    }

    private async Task<EnrollmentInstructions> IssueTokenAsync(Node node, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (token, plain) = NodeEnrollmentToken.Issue(node.Id, request.UserId, now);
        db.NodeEnrollmentTokens.Add(token);
        audit.Record(AuditActions.Node.EnrollmentTokenCreated, metadata: new { expiresAt = token.ExpiresAt })
            .WithNode(node.Id).WithTarget("node", node.Id.ToString());
        var panelKey = (await keys.GetPublicKeysAsync(cancellationToken))[0];
        return new EnrollmentInstructions(plain, token.ExpiresAt, panelKey.KeyId);
    }

    /// <summary>
    /// Saves an administrator's change to the node. Its agent writes the same row (Hello, last seen), which makes a save
    /// that read it before fail: the change is applied again to the fresh row, so a disable or a deletion is not lost.
    /// False when the node was deleted meanwhile.
    /// </summary>
    private async Task<bool> SaveNodeAsync(Node node, Action<Node> apply, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                await db.Entry(node).ReloadAsync(cancellationToken);
                if (db.Entry(node).State == EntityState.Detached)
                {
                    return false;
                }

                apply(node);
            }
        }
    }

    /// <summary>Ends the node's sessions and closes its connection right away.</summary>
    private void CutOff(NodeId id)
    {
        sessions.EndAll(id);
        connections.Disconnect(id);
    }

    private async Task<string> ActiveKeyIdAsync(CancellationToken cancellationToken) =>
        (await keys.GetPublicKeysAsync(cancellationToken))[0].KeyId;

    private async Task<NodeSummary> SummarizeAsync(Node node, CancellationToken cancellationToken) =>
        (await SummarizeAsync([node], cancellationToken))[0];

    /// <summary>The nodes as the UI lists them, with their active key and the reusable token that created them (to approve them knowingly).</summary>
    private async Task<List<NodeSummary>> SummarizeAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken)
    {
        var activeKeyId = await ActiveKeyIdAsync(cancellationToken);
        var ids = nodes.Select(n => n.Id).ToList();
        var fingerprints = await db.NodeCredentials.AsNoTracking()
            .Where(c => ids.Contains(c.NodeId) && c.Status == NodeCredentialStatus.Active)
            .Select(c => new { c.NodeId, c.Fingerprint })
            .ToDictionaryAsync(c => c.NodeId, c => c.Fingerprint, cancellationToken);
        var tokenIds = nodes.Where(n => n.EnrollmentTokenId is not null).Select(n => n.EnrollmentTokenId!.Value).Distinct().ToList();
        var tokens = tokenIds.Count == 0
            ? []
            : await db.ReusableEnrollmentTokens.AsNoTracking().Where(t => tokenIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        return [.. nodes.Select(n => Summarize(n, activeKeyId, n.EnrollmentTokenId is { } token ? tokens.GetValueOrDefault(token) : null,
            fingerprints.GetValueOrDefault(n.Id)))];
    }

    private NodeSummary Summarize(Node node, string activeKeyId, string? enrollmentTokenName, string? credentialFingerprint)
    {
        // A connection whose session ended (revocation, key rotation) is about to close or be replaced: not connected.
        var connection = connections.Find(node.Id) is { } live && sessions.IsActive(live.SessionId) ? live : null;
        var inventory = node.Inventory is { } i
            ? new NodeInventoryView(i.Hostname, i.OsDescription, i.OsBuild, i.CpuModel, i.LogicalCores, i.MemoryTotalBytes, i.ServersRoot, i.AgentVersion, i.ProtocolVersion)
            : null;
        var metrics = connection?.LastMetrics is { } m ? NodeMetricsView.From(m) : null;
        return new NodeSummary(node.Id.Value, node.Name, node.Description, node.PublicHost, node.Status, connection is not null,
            node.LastSeenAt, inventory, metrics, node.CreatedAt, Trust(connection?.PinnedPanelKeys ?? node.PinnedPanelKeys, activeKeyId),
            enrollmentTokenName, credentialFingerprint, node.ConnectedWithCertificate, node.AgentCanUseCertificate);
    }

    private static PanelKeyTrust Trust(IReadOnlyList<string>? pinned, string activeKeyId) => pinned switch
    {
        null => PanelKeyTrust.Unknown,
        _ when pinned.Contains(activeKeyId, StringComparer.Ordinal) => PanelKeyTrust.Current,
        _ => PanelKeyTrust.Outdated,
    };
}
