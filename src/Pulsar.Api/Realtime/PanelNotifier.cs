using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Realtime;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Realtime;

/// <summary>
/// Sends <see cref="IRealtimeNotifier"/> notices to the browsers watching through <see cref="PanelHub"/>: every notice
/// to the watchers with global roles, and each server's or node's to those who see it through a role on it or on its
/// node, so nobody learns about servers or nodes they cannot see.
/// </summary>
internal sealed partial class PanelNotifier(
    IHubContext<PanelHub, IPanelClient> hub,
    BrowserConnections connections,
    IServiceScopeFactory scopes,
    ILogger<PanelNotifier> logger) : IRealtimeNotifier
{
    public void ServersChanged(IReadOnlyCollection<ServerId> servers, IReadOnlyDictionary<ServerId, NodeId>? nodes = null)
    {
        Send(hub.Clients.Group(PanelHub.ServersGroup).ServersChanged([.. servers.Select(s => s.Value)]));
        if (connections.AnyScopedWatcher)
        {
            Send(ScopedServersChangedAsync(servers, nodes ?? new Dictionary<ServerId, NodeId>()));
        }
    }

    public void NodesChanged(IReadOnlyCollection<NodeId> nodes)
    {
        Send(hub.Clients.Groups(PanelHub.NodesGroup, PanelHub.NodesListGroup).NodesChanged([.. nodes.Select(n => n.Value)]));
        if (connections.AnyScopedWatcher)
        {
            Send(Task.WhenAll(nodes.Select(node =>
                hub.Clients.Groups(PanelHub.NodeGroup(node), PanelHub.NodeListGroup(node)).NodesChanged([node.Value]))));
        }
    }

    private async Task ScopedServersChangedAsync(IReadOnlyCollection<ServerId> servers, IReadOnlyDictionary<ServerId, NodeId> known)
    {
        var nodes = new Dictionary<ServerId, NodeId>(known);
        var unknown = servers.Where(s => !nodes.ContainsKey(s)).ToList();
        if (unknown.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            foreach (var server in await db.Servers.AsNoTracking().Where(s => unknown.Contains(s.Id)).Select(s => new { s.Id, s.NodeId }).ToListAsync())
            {
                nodes[server.Id] = server.NodeId;
            }
        }

        var sends = servers.Select(s => hub.Clients.Group(PanelHub.ServerListGroup(s)).ServersChanged([s.Value])).ToList();
        sends.AddRange(servers.Where(nodes.ContainsKey).GroupBy(s => nodes[s])
            .Select(onNode => hub.Clients.Group(PanelHub.NodeServersGroup(onNode.Key)).ServersChanged([.. onNode.Select(s => s.Value)])));
        await Task.WhenAll(sends);
    }

    public void OperationProgress(ServerId server, Guid operationId, string stage, decimal? percent) =>
        Send(hub.Clients.Group(PanelHub.ServerGroup(server)).OperationProgress(new OperationProgressView(server.Value, operationId, stage, percent)));

    private void Send(Task sending) => _ = sending.ContinueWith(t => LogSendFailed(t.Exception!.GetBaseException()), CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not notify browsers")]
    private partial void LogSendFailed(Exception exception);
}
