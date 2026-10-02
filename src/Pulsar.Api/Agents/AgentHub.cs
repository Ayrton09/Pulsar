using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Api.Agents;

/// <summary>The authenticated agent channel; a connection becomes available for commands after its Hello.</summary>
[Authorize(AuthenticationSchemes = AgentAuthenticationHandler.Scheme)]
internal sealed partial class AgentHub(
    IPulsarDbContext db,
    AgentConnectionRegistry connections,
    AgentSessionStore sessions,
    IPanelKeyRing keys,
    TimeProvider clock,
    ILogger<AgentHub> logger) : Hub, IAgentHub
{
    private static readonly object s_lastSeenAtKey = new();
    private static readonly TimeSpan s_seenInterval = TimeSpan.FromMinutes(1);

    public override async Task OnConnectedAsync()
    {
        var principal = Context.User!;
        var nodeId = principal.NodeId();
        var credentialId = principal.CredentialId();
        // Authentication may have run before a credential was revoked or its node disabled.
        var allowed = await db.NodeCredentials.AnyAsync(c => c.Id == credentialId && c.NodeId == nodeId
            && c.Status == NodeCredentialStatus.Active
            && db.Nodes.Any(n => n.Id == nodeId && n.Status == NodeStatus.Active), Context.ConnectionAborted);
        if (!allowed)
        {
            LogConnectionRejected(nodeId, "credential revoked or node inactive");
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public async Task<HelloAccepted> Hello(AgentHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var principal = Context.User!;
        var nodeId = principal.NodeId();
        if (hello.ProtocolVersion is < AgentProtocol.MinimumSupportedVersion or > AgentProtocol.CurrentVersion)
        {
            LogProtocolRejected(nodeId, hello.ProtocolVersion, AgentProtocol.MinimumSupportedVersion, AgentProtocol.CurrentVersion);
            Context.Abort();
            throw new HubException("Unsupported agent protocol version; update the agent.");
        }

        var credentialId = principal.CredentialId();
        var session = sessions.Find(principal.SessionId());
        if (session is null || session.NodeId != nodeId || session.CredentialId != credentialId)
        {
            LogConnectionRejected(nodeId, "session unknown, expired or ended");
            Context.Abort();
            throw new HubException("Agent session is no longer active; authenticate again.");
        }

        // Re-check at Hello too: revocation can happen while waiting for the first message.
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == nodeId, Context.ConnectionAborted);
        var credentialActive = await db.NodeCredentials.AnyAsync(c => c.Id == credentialId && c.NodeId == nodeId
            && c.Status == NodeCredentialStatus.Active, Context.ConnectionAborted);
        if (node is not { AcceptsConnections: true } || !credentialActive)
        {
            LogConnectionRejected(nodeId, "credential revoked or node inactive at Hello");
            Context.Abort();
            throw new HubException("The node cannot connect.");
        }

        var panelKeys = await keys.GetPublicKeysAsync(Context.ConnectionAborted);
        var now = clock.GetUtcNow();
        node.Connected(AgentAccess.ToSnapshot(hello.Inventory, hello.AgentVersion, hello.ProtocolVersion), now);
        if (hello.PinnedPanelKeys is { } pinned)
        {
            node.PanelKeysPinned(pinned);
        }

        await db.SaveChangesAsync(Context.ConnectionAborted);

        // Hub instances are transient; Items keeps the throttle across this connection's invocations.
        Context.Items[s_lastSeenAtKey] = now;
        connections.Register(Context, session, new AgentConnectionInfo(nodeId, session.SessionId, now,
            hello.AgentVersion, hello.ProtocolVersion, hello.Capabilities, null, node.PinnedPanelKeys));

        // A revocation between the checks above and the registration ended the session but found nothing to close.
        if (!sessions.IsActive(session.SessionId))
        {
            connections.Disconnect(nodeId);
            throw new HubException("Agent session is no longer active; authenticate again.");
        }

        // An agent that missed a panel key rotation gets the new keys. Not awaited here: waiting for a client result
        // inside this connection's own hub method would deadlock (one invocation per client at a time).
        if (hello.PinnedPanelKeys is not null)
        {
            _ = Task.Run(() => UpdatePanelKeysAsync(nodeId));
        }

        return new HelloAccepted(now, panelKeys);
    }

    private async Task UpdatePanelKeysAsync(NodeId nodeId)
    {
        try
        {
            await connections.UpdatePanelKeysAsync(nodeId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or OperationCanceledException)
        {
            LogPanelKeysUpdateFailed(ex, nodeId);
        }
    }

    public async Task NodeMetrics(NodeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var nodeId = Context.User!.NodeId();
        if (!connections.IsRegistered(nodeId, Context.ConnectionId))
        {
            return;
        }

        connections.UpdateMetrics(nodeId, Context.ConnectionId, metrics);
        var now = clock.GetUtcNow();
        if (Context.Items.TryGetValue(s_lastSeenAtKey, out var value)
            && value is DateTimeOffset lastSeenAt && now - lastSeenAt < s_seenInterval)
        {
            return;
        }

        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == nodeId, Context.ConnectionAborted);
        if (node is null)
        {
            return;
        }

        // A reconnect or authentication may have already refreshed the persisted timestamp.
        if (node.LastSeenAt is { } seenAt && now - seenAt < s_seenInterval)
        {
            Context.Items[s_lastSeenAtKey] = seenAt;
            return;
        }

        node.Seen(now);
        await db.SaveChangesAsync(Context.ConnectionAborted);
        Context.Items[s_lastSeenAtKey] = now;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Unregister(Context.User!.NodeId(), Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent connection rejected for node {NodeId}: {Reason}")]
    private partial void LogConnectionRejected(NodeId nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Node {NodeId} rejected with protocol version {ProtocolVersion}; supported versions are {MinimumVersion} through {CurrentVersion}")]
    private partial void LogProtocolRejected(NodeId nodeId, int protocolVersion, int minimumVersion, int currentVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the panel keys to node {NodeId}")]
    private partial void LogPanelKeysUpdateFailed(Exception exception, NodeId nodeId);
}
