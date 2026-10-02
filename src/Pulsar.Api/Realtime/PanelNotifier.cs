using Microsoft.AspNetCore.SignalR;
using Pulsar.Application.Realtime;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Realtime;

/// <summary>Sends <see cref="IRealtimeNotifier"/> notices to the browsers watching through <see cref="PanelHub"/>.</summary>
internal sealed partial class PanelNotifier(IHubContext<PanelHub, IPanelClient> hub, ILogger<PanelNotifier> logger) : IRealtimeNotifier
{
    public void ServersChanged(IReadOnlyCollection<ServerId> servers) =>
        Send(hub.Clients.Group(PanelHub.ServersGroup).ServersChanged([.. servers.Select(s => s.Value)]));

    public void NodesChanged(IReadOnlyCollection<NodeId> nodes) =>
        Send(hub.Clients.Group(PanelHub.NodesGroup).NodesChanged([.. nodes.Select(n => n.Value)]));

    public void OperationProgress(ServerId server, Guid operationId, string stage, decimal? percent) =>
        Send(hub.Clients.Group(PanelHub.ServerGroup(server)).OperationProgress(new OperationProgressView(server.Value, operationId, stage, percent)));

    private void Send(Task sending) => _ = sending.ContinueWith(t => LogSendFailed(t.Exception!.GetBaseException()), CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not notify browsers")]
    private partial void LogSendFailed(Exception exception);
}
