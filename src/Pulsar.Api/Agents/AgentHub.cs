using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Realtime;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Metrics;
using Pulsar.Application.Nodes;
using Pulsar.Application.Realtime;
using Pulsar.Application.Servers;
using Pulsar.Application.Updates;
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
    AgentServerEvents servers,
    ConsoleMirrors consoles,
    LiveMetrics liveMetrics,
    MetricHistory history,
    FileTransfers transfers,
    IRealtimeNotifier notifier,
    IHubContext<PanelHub, IPanelClient> browsers,
    IServiceScopeFactory scopes,
    AgentsEndpoint agents,
    TimeProvider clock,
    ILogger<AgentHub> logger) : Hub, IAgentHub
{
    /// <summary>The most servers one metrics batch is read for.</summary>
    internal const int MaxServersPerBatch = 512;

    private static readonly object s_lastSeenAtKey = new();
    private static readonly object s_registeredKey = new();
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

        // Whoever holds a session token can say Hello: what it says it pins is bounded, as at sign-in, and never recorded.
        if (hello.PinnedPanelKeys is not null && !AgentProtocol.ArePinnedPanelKeyIds(hello.PinnedPanelKeys))
        {
            LogConnectionRejected(nodeId, "malformed pinned panel key ids");
            Context.Abort();
            throw new HubException("The panel keys this agent pins are not well-formed.");
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
        // Absent in the JSON of a faulty agent, or longer than any agent's: what it says it can do, bounded.
        IReadOnlyList<string> capabilities = [.. (hello.Capabilities ?? []).OfType<string>().Take(AgentHello.MaxCapabilities)];
        var canMove = capabilities.Contains(AgentCapabilities.UseAgentsAddress, StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        var inventory = AgentAccess.ToSnapshot(hello.Inventory, hello.AgentVersion, hello.ProtocolVersion);
        // The keys this connection says it pins sign its commands; the node's record of them comes from its sign-ins only,
        // which the node key signs (whoever holds a session token can say Hello).
        IReadOnlyList<string>? pinned = hello.PinnedPanelKeys is { } said ? [.. said.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] : null;
        if (pinned is not null && !(node.PinnedPanelKeys ?? []).SequenceEqual(pinned, StringComparer.Ordinal))
        {
            LogPinsDiffer(nodeId, pinned, node.PinnedPanelKeys);
        }

        for (var attempt = 0; ; attempt++)
        {
            node.Connected(inventory, now);
            node.ConnectedThrough(session.CertificateKey is not null, canMove);
            try
            {
                await db.SaveChangesAsync(Context.ConnectionAborted);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // The node's row changed meanwhile (a sign-in recorded its keys): apply the Hello to the fresh row.
                await db.Entry(node).ReloadAsync(Context.ConnectionAborted);
            }
        }

        // Hub instances are transient; Items keeps the throttle across this connection's invocations.
        Context.Items[s_lastSeenAtKey] = now;
        Context.Items[s_registeredKey] = true;
        connections.Register(Context, session, new AgentConnectionInfo(nodeId, session.SessionId, now,
            inventory.AgentVersion, hello.ProtocolVersion, capabilities, null, pinned ?? node.PinnedPanelKeys));

        // A revocation between the checks above and the registration ended the session but found nothing to close.
        if (!sessions.IsActive(session.SessionId))
        {
            connections.Disconnect(nodeId);
            throw new HubException("Agent session is no longer active; authenticate again.");
        }

        // The agent's view of its servers wins (docs/architecture/05, "Reconciliation").
        await servers.ReconcileAsync(nodeId, hello.Servers ?? [], hello.OperationsInFlight ?? [], Context.ConnectionAborted);
        notifier.NodesChanged([nodeId]);

        // An agent that missed a panel key rotation gets the new keys. Not awaited here: waiting for a client result
        // inside this connection's own hub method would deadlock (one invocation per client at a time).
        if (hello.PinnedPanelKeys is not null)
        {
            _ = Task.Run(() => UpdatePanelKeysAsync(nodeId));
        }

        // An agent that can present its key as a client certificate, connected without one while the panel has an
        // agents' endpoint, is told its address (Phase 16); it signs in there before it keeps it. Not awaited either.
        if (session.CertificateKey is null && canMove && agents.Address is { } address)
        {
            _ = Task.Run(() => UseAgentsAddressAsync(nodeId, address));
        }

        // The cached logins of Steam accounts deleted while the agent was away: forgotten by signed commands (this answer is
        // not signed, so the agent acts on no list in it). Not awaited either.
        if (hello.CachedSteamLogins is { Count: > 0 } cached && capabilities.Contains(AgentCapabilities.ForgetSteamLogin, StringComparer.Ordinal))
        {
            var known = (await db.SteamAccounts.AsNoTracking().Select(a => a.Id).ToListAsync(Context.ConnectionAborted)).Select(id => id.Value).ToHashSet();
            var deleted = cached.Take(AgentHello.MaxCachedSteamLogins).Distinct().Where(id => !known.Contains(id)).ToList();
            if (deleted.Count > 0)
            {
                _ = Task.Run(() => ForgetSteamLoginsAsync(nodeId, deleted));
            }
        }

        // Consoles browsers were watching (before a reconnect or a panel restart) resume where the panel's copy ends.
        return new HelloAccepted(now, panelKeys, consoles.SubscriptionsFor(nodeId));
    }

    private async Task ForgetSteamLoginsAsync(NodeId nodeId, IReadOnlyList<Guid> accounts)
    {
        try
        {
            foreach (var account in accounts)
            {
                await connections.SendAsync(nodeId, new ForgetSteamLoginCommand(account), null, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogSteamLoginsNotForgotten(ex, nodeId);
        }
    }

    private async Task UpdatePanelKeysAsync(NodeId nodeId)
    {
        try
        {
            await connections.UpdatePanelKeysAsync(nodeId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Not awaited by anyone: whatever goes wrong is logged here or never seen (also in the follow-ups below).
            LogPanelKeysUpdateFailed(ex, nodeId);
        }
    }

    public async Task NodeMetrics(NodeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        metrics = AgentInput.Metrics(metrics);
        if (RegisteredNode() is not { } nodeId)
        {
            return;
        }

        connections.UpdateMetrics(nodeId, Context.ConnectionId, metrics);
        history.RecordNode(nodeId, metrics.CpuPercent, metrics.MemoryUsedBytes, metrics.MemoryTotalBytes);
        var update = new NodeMetricsUpdate(nodeId.Value, NodeMetricsView.From(metrics));
        await browsers.Clients.Groups(PanelHub.NodesGroup, PanelHub.NodeGroup(nodeId)).NodeMetrics(update);
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
        try
        {
            await db.SaveChangesAsync(Context.ConnectionAborted);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row changed meanwhile; "last seen" is advisory and is written again a minute from now.
            return;
        }

        Context.Items[s_lastSeenAtKey] = now;
    }

    // Events are stored even when the connection closes right after them (an agent stopping sends its servers' last
    // states and leaves): they are not tied to the connection's lifetime.
    public async Task ServerStateChanged(ServerStateChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change = AgentInput.StateChanged(change);
        if (RegisteredNode() is { } nodeId)
        {
            // Only its own servers' samples go: another node's would otherwise show blank to whoever opens its page.
            if (await servers.StateChangedAsync(nodeId, change, CancellationToken.None) && change.State is RuntimeState.Offline or RuntimeState.Crashed)
            {
                liveMetrics.Remove(new ServerId(change.ServerId));
            }
        }
    }

    /// <summary>Output of a console browsers watch; the mirrors drop anything about servers not of this node.</summary>
    public async Task ConsoleOutput(ServerConsoleOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (RegisteredNode() is { } nodeId)
        {
            await consoles.ReceiveAsync(nodeId, output);
        }
    }

    /// <summary>The end of a file the agent received: answers the browser's upload waiting for it.</summary>
    public Task TransferCompleted(TransferResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result = AgentInput.Transfer(result);
        if (RegisteredNode() is { } nodeId)
        {
            transfers.Complete(nodeId, result);
        }

        return Task.CompletedTask;
    }

    /// <summary>How an update of Pulsar on the node goes (Phase 19): kept on the node's row, its end audited.</summary>
    public async Task UpdateReport(UpdateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (RegisteredNode() is not { } nodeId || !UpdateStatuses.All.Contains(report.Status ?? string.Empty))
        {
            return;
        }

        LogUpdateReport(nodeId, report.Status!);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PanelUpdates>().ReportAsync(nodeId, report, CancellationToken.None);
    }

    /// <summary>What a node's check for game updates found (Pulsar 1.0.9): kept on the server's watch, a new update notified.</summary>
    public async Task GameUpdateChecked(GameUpdateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (RegisteredNode() is not { } nodeId)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        // Before anything resolves the request context: what it starts is audited as the game updates.
        scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = new SystemRequestContext(GameUpdates.ActorName);
        await scope.ServiceProvider.GetRequiredService<GameUpdates>().ReportedAsync(nodeId, check, CancellationToken.None);
    }

    /// <summary>Usage of the node's running servers: kept for pages that open later, and pushed to the ones watching.</summary>
    public async Task Metrics(MetricsBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        // One sample per server, as many as a node hosts at most: a batch of thousands (or of one server over and over)
        // would cost a query that size and a push to browsers for each.
        var samples = (batch.Servers ?? []).OfType<ServerMetrics>().DistinctBy(s => s.ServerId).Take(MaxServersPerBatch).ToList();
        if (RegisteredNode() is not { } nodeId || samples.Count == 0)
        {
            return;
        }

        var reported = samples.Select(s => new ServerId(s.ServerId)).ToList();
        var own = (await db.Servers.Where(s => s.NodeId == nodeId && reported.Contains(s.Id)).Select(s => s.Id).ToListAsync(CancellationToken.None))
            .ToHashSet();
        // When the panel received it, not when the agent says it sampled: a node's clock decides nothing of how fresh it is.
        var at = clock.GetUtcNow();
        foreach (var sample in samples.Where(s => own.Contains(new ServerId(s.ServerId))))
        {
            var view = new ServerMetricsView(sample.ServerId, at, MetricHistory.CpuPercent(sample.CpuPercent), sample.MemoryBytes, sample.ProcessCount,
                sample.Uptime.TotalSeconds,
                ServerQueryView.From(sample.Query), sample.CommittedBytes);
            liveMetrics.Update(view);
            history.RecordServer(new ServerId(sample.ServerId), view.CpuPercent, view.MemoryBytes, view.Query?.Players);
            await browsers.Clients.Group(PanelHub.ServerGroup(new ServerId(sample.ServerId))).ServerMetrics(view);
        }
    }

    public async Task OperationProgress(OperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress = AgentInput.Progress(progress);
        if (RegisteredNode() is { } nodeId)
        {
            await servers.OperationProgressAsync(nodeId, progress, CancellationToken.None);
        }
    }

    /// <summary>Completes only once the result is stored: the agent deletes it from its outbox when this call returns.</summary>
    public async Task OperationCompleted(OperationCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        completed = AgentInput.Completed(completed);
        if (RegisteredNode() is not { } nodeId)
        {
            throw new HubException("Say Hello first.");
        }

        if (await servers.OperationCompletedAsync(nodeId, completed, CancellationToken.None) is { } followUp)
        {
            // The install that follows provisioning, and retention after a backup, send commands to this same agent: not
            // awaited inside its own call.
            _ = Task.Run(() => FollowUpAsync(followUp));
        }
    }

    /// <summary>The node, while this connection said Hello and its session lasts (a revocation or rotation ends it before the connection closes).</summary>
    private NodeId? RegisteredNode()
    {
        var nodeId = Context.User!.NodeId();
        return connections.IsRegistered(nodeId, Context.ConnectionId) && sessions.IsActive(Context.User!.SessionId()) ? nodeId : null;
    }

    private async Task FollowUpAsync(ServerFollowUp followUp)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (followUp.Kind == ServerFollowUpKind.PruneBackups)
            {
                await scope.ServiceProvider.GetRequiredService<ServerBackups>().PruneAsync(followUp.ServerId, CancellationToken.None);
            }
            else
            {
                // Provisioned: whoever watches its console from the start sees the install.
                consoles.Provisioned(followUp.ServerId);
                await scope.ServiceProvider.GetRequiredService<ServerManagement>().ContinueInstallAsync(followUp.ServerId, followUp.RequestedBy,
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogFollowUpFailed(ex, followUp.Kind, followUp.ServerId);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var nodeId = Context.User!.NodeId();
        connections.Unregister(nodeId, Context.ConnectionId);
        // Whether the agent left or the panel ended it (a revocation, a disable: its entry is gone already), unless a newer
        // connection of the node took its place: browsers see the node gone and its consoles stop.
        if (Context.Items.ContainsKey(s_registeredKey) && connections.Find(nodeId) is null)
        {
            consoles.NodeDisconnected(nodeId);
            notifier.NodesChanged([nodeId]);
        }

        return base.OnDisconnectedAsync(exception);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} reports an update of Pulsar: {Status}")]
    private partial void LogUpdateReport(NodeId nodeId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent connection rejected for node {NodeId}: {Reason}")]
    private partial void LogConnectionRejected(NodeId nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Node {NodeId} rejected with protocol version {ProtocolVersion}; supported versions are {MinimumVersion} through {CurrentVersion}")]
    private partial void LogProtocolRejected(NodeId nodeId, int protocolVersion, int minimumVersion, int currentVersion);

    [LoggerMessage(Level = LogLevel.Error, Message = "Follow-up {Kind} of server {ServerId} could not be done (after provisioning: its install did not start)")]
    private partial void LogFollowUpFailed(Exception exception, ServerFollowUpKind kind, ServerId serverId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Node {NodeId}'s connection says it pins the panel keys {Said}, not those its last sign-in signed ({Recorded})")]
    private partial void LogPinsDiffer(NodeId nodeId, IReadOnlyList<string> said, IReadOnlyList<string>? recorded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the panel keys to node {NodeId}")]
    private partial void LogPanelKeysUpdateFailed(Exception exception, NodeId nodeId);

    private async Task UseAgentsAddressAsync(NodeId nodeId, AgentsEndpointAddress address)
    {
        try
        {
            var ack = await connections.SendAsync(nodeId, new UseAgentsAddressCommand(address.Port, address.Url), null, CancellationToken.None);
            LogAgentsAddressSent(nodeId, address.Port, address.Url, ack?.Status, ack?.ErrorCode);
        }
        catch (Exception ex)
        {
            LogAgentsAddressFailed(ex, nodeId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} was told the agents' address (port {Port}, address {Url}): {Status} {ErrorCode}")]
    private partial void LogAgentsAddressSent(NodeId nodeId, int port, string? url, AckStatus? status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the agents' address to node {NodeId}")]
    private partial void LogAgentsAddressFailed(Exception exception, NodeId nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not tell node {NodeId} to forget the cached logins of deleted Steam accounts")]
    private partial void LogSteamLoginsNotForgotten(Exception exception, NodeId nodeId);
}
