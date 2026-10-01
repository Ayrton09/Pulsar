using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
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

public sealed record NodeMetricsView(DateTimeOffset At, double CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, TimeSpan Uptime);

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
    DateTimeOffset CreatedAt);

public sealed record NodeCredentialView(
    Guid Id,
    string Fingerprint,
    NodeKeyStorage KeyStorage,
    NodeCredentialStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

public sealed record NodeDetails(NodeSummary Node, IReadOnlyList<NodeCredentialView> Credentials);

/// <summary>
/// Shown once to the administrator: the token and the fingerprint of the panel key, which the agent pins
/// (<c>PulsarAgent.exe enroll --panel … --token … --panel-key …</c>).
/// </summary>
public sealed record EnrollmentInstructions(string Token, DateTimeOffset ExpiresAt, string PanelKeyFingerprint);

public sealed record CreatedNode(NodeSummary Node, EnrollmentInstructions Enrollment);

public sealed record PingResult(AckStatus Status, double RoundTripMilliseconds);

/// <summary>Node administration from the UI (docs/architecture/03). Requires a signed-in actor.</summary>
public sealed class NodeManagement(
    IPulsarDbContext db,
    IPanelKeyRing keys,
    IAgentConnections connections,
    AgentSessionStore sessions,
    IRequestContext request,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<NodeSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var nodes = await db.Nodes.AsNoTracking().OrderBy(n => n.NormalizedName).ToListAsync(cancellationToken);
        return [.. nodes.Select(Summarize)];
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
            .Select(c => new NodeCredentialView(c.Id, c.Fingerprint, c.KeyStorage, c.Status, c.CreatedAt, c.LastUsedAt, c.RevokedAt, c.RevokeReason))
            .ToListAsync(cancellationToken);
        return new NodeDetails(Summarize(node), credentials);
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
        return new CreatedNode(Summarize(node), instructions);
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
        return Summarize(node);
    }

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

        return Summarize(node);
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

    private NodeSummary Summarize(Node node)
    {
        var connection = connections.Find(node.Id);
        var inventory = node.Inventory is { } i
            ? new NodeInventoryView(i.Hostname, i.OsDescription, i.OsBuild, i.CpuModel, i.LogicalCores, i.MemoryTotalBytes, i.ServersRoot, i.AgentVersion, i.ProtocolVersion)
            : null;
        var metrics = connection?.LastMetrics is { } m
            ? new NodeMetricsView(m.At, m.CpuPercent, m.MemoryUsedBytes, m.MemoryTotalBytes, m.Uptime)
            : null;
        return new NodeSummary(node.Id.Value, node.Name, node.Description, node.PublicHost, node.Status, connection is not null,
            node.LastSeenAt, inventory, metrics, node.CreatedAt);
    }
}
