using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Agents;

/// <summary>
/// Connected agents: one connection per node, in memory (rebuilt from every Hello after a panel restart).
/// Also the <see cref="IAgentConnections"/> used by application code to reach agents.
/// </summary>
internal sealed partial class AgentConnectionRegistry(
    IHubContext<AgentHub> hub,
    IPanelKeyRing keys,
    AgentSessionStore sessions,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<AgentConnectionRegistry> logger) : IAgentConnections
{
    /// <summary>How long the panel waits for an ack (the ack means accepted, not finished).</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Lifetime of a signed command; a secondary defense next to the session sequence.</summary>
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<NodeId, Connection> _connections = new();

    private sealed record Connection(string ConnectionId, HubCallerContext Context, AgentSession Session, AgentConnectionInfo Info);

    /// <summary>Registers a connection after its Hello; a previous connection of the same node is closed.</summary>
    public void Register(HubCallerContext context, AgentSession session, AgentConnectionInfo info)
    {
        var connection = new Connection(context.ConnectionId, context, session, info);
        _connections.AddOrUpdate(info.NodeId, connection, (_, previous) =>
        {
            if (previous.ConnectionId != context.ConnectionId)
            {
                LogReplaced(info.NodeId);
                previous.Context.Abort();
            }

            return connection;
        });
    }

    /// <summary>Removes the node's entry if it still belongs to <paramref name="connectionId"/>.</summary>
    public bool Unregister(NodeId node, string connectionId) =>
        _connections.TryGetValue(node, out var current) && current.ConnectionId == connectionId
        && _connections.TryRemove(new KeyValuePair<NodeId, Connection>(node, current));

    public bool IsRegistered(NodeId node, string connectionId) =>
        _connections.TryGetValue(node, out var current) && current.ConnectionId == connectionId;

    public void UpdateMetrics(NodeId node, string connectionId, NodeMetrics metrics)
    {
        if (_connections.TryGetValue(node, out var current) && current.ConnectionId == connectionId)
        {
            _connections.TryUpdate(node, current with { Info = current.Info with { LastMetrics = metrics } }, current);
        }
    }

    public AgentConnectionInfo? Find(NodeId node) => _connections.TryGetValue(node, out var connection) ? connection.Info : null;

    public void Disconnect(NodeId node)
    {
        if (_connections.TryRemove(node, out var connection))
        {
            connection.Context.Abort();
        }
    }

    public async Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_connections.TryGetValue(node, out var connection)
            || !connection.Info.Capabilities.Contains(AgentCapabilities.Of(command), StringComparer.Ordinal)
            || !sessions.IsActive(connection.Session.SessionId))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var envelope = new CommandEnvelope(AgentProtocol.CurrentVersion, Guid.CreateVersion7(now), node.Value, connection.Session.SessionId,
            connection.Session.NextSequence(), now, now + CommandLifetime, null, actor?.Value, command);
        var signed = await keys.SignForAsync(AgentProtocol.Purposes.Command, ProtocolJson.Serialize(envelope), connection.Info.PinnedPanelKeys, cancellationToken);
        if (signed is null)
        {
            LogNoTrustedKey(node);
            return null;
        }


        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AckTimeout);
        try
        {
            return await hub.Clients.Client(connection.ConnectionId)
                .InvokeAsync<CommandAck>(nameof(IAgentClient.Execute), signed, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HubException && !cancellationToken.IsCancellationRequested)
        {
            LogNoAck(node, envelope.CommandId, ex.GetType().Name);
            return null;
        }
    }

    public async Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken)
    {
        var current = await keys.GetPublicKeysAsync(cancellationToken);
        var currentIds = current.Select(k => k.KeyId).Order(StringComparer.Ordinal).ToList();
        // Agents that do not report their keys predate rotation and could not take the command either.
        var targets = _connections.Values
            .Where(c => (node is null || c.Info.NodeId == node) && c.Info.PinnedPanelKeys is { } pinned
                && !pinned.Order(StringComparer.Ordinal).SequenceEqual(currentIds, StringComparer.Ordinal))
            .ToList();
        var results = await Task.WhenAll(targets.Select(async connection =>
        {
            var ack = await SendAsync(connection.Info.NodeId, new UpdatePanelKeysCommand(current), null, cancellationToken);
            if (ack?.Status is AckStatus.Accepted or AckStatus.AlreadyInState)
            {
                await RecordPinnedKeysAsync(connection.Info.NodeId, connection.ConnectionId, currentIds, cancellationToken);
                return true;
            }

            LogKeysNotUpdated(connection.Info.NodeId, ack?.Status.ToString() ?? "no answer");
            return false;
        }));
        return results.Count(updated => updated);
    }

    /// <summary>Remembers the keys an agent pins, in memory (signing) and on the node (the administrator's overview).</summary>
    public async Task RecordPinnedKeysAsync(NodeId node, string connectionId, IReadOnlyList<string> keyIds, CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(node, out var current) && current.ConnectionId == connectionId)
        {
            _connections.TryUpdate(node, current with { Info = current.Info with { PinnedPanelKeys = keyIds } }, current);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var stored = await db.Nodes.SingleOrDefaultAsync(n => n.Id == node, cancellationToken);
        if (stored is not null)
        {
            stored.PanelKeysPinned(keyIds);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} connected again; closing its previous connection")]
    private partial void LogReplaced(NodeId nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} trusts none of the panel's keys (it missed a key rotation); re-enroll it")]
    private partial void LogNoTrustedKey(NodeId nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} did not take the new panel keys: {Status}")]
    private partial void LogKeysNotUpdated(NodeId nodeId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No ack from node {NodeId} for command {CommandId} ({Reason})")]
    private partial void LogNoAck(NodeId nodeId, Guid commandId, string reason);
}
