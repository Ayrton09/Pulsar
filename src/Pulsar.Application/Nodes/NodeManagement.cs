using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
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
        return new NodeMetricsView(metrics.At, metrics.CpuPercent, metrics.MemoryUsedBytes, metrics.MemoryTotalBytes, metrics.Uptime,
            metrics.Disks?.Select(d => new DiskUsageView(d.Root, d.TotalBytes, d.FreeBytes, d.Uses)).ToList());
    }
}

/// <param name="EnrollmentTokenName">The reusable enrollment token that created the node, when one did (and still exists).</param>
/// <param name="CredentialFingerprint">The fingerprint of the node's active key, to check a machine before approving it.</param>
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
    string? CredentialFingerprint);

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

        var updated = node.Update(name ?? string.Empty, description, publicHost, clock.GetUtcNow());
        if (!updated.IsSuccess)
        {
            return updated.Error!;
        }

        audit.Record(AuditActions.Node.Updated, metadata: new { name = node.Name }).WithNode(node.Id).WithTarget("node", node.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
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

    public async Task<Result> RevokeCredentialAsync(NodeId id, Guid credentialId, string? reason, CancellationToken cancellationToken)
    {
        var credential = await db.NodeCredentials.SingleOrDefaultAsync(c => c.Id == credentialId && c.NodeId == id, cancellationToken);
        if (credential is null)
        {
            return NodeErrors.NotFound;
        }

        credential.Revoke(request.UserId, reason, clock.GetUtcNow());
        audit.Record(AuditActions.Node.CredentialRevoked, metadata: new { credential = credential.Id, fingerprint = credential.Fingerprint, reason })
            .WithNode(id).WithTarget("node", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
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
            enrollmentTokenName, credentialFingerprint);
    }

    private static PanelKeyTrust Trust(IReadOnlyList<string>? pinned, string activeKeyId) => pinned switch
    {
        null => PanelKeyTrust.Unknown,
        _ when pinned.Contains(activeKeyId, StringComparer.Ordinal) => PanelKeyTrust.Current,
        _ => PanelKeyTrust.Outdated,
    };
}
