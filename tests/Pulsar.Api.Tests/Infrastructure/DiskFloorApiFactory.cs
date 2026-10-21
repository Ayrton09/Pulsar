using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The panel, seeing the drives a test sets for a node (<see cref="Disks"/>) in its agent's last metrics instead of the
/// ones the in-process agent reports (this machine's).
/// </summary>
public sealed class DiskFloorApiFactory : PulsarApiFactory
{
    public ConcurrentDictionary<NodeId, IReadOnlyList<DiskUsage>> Disks { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
            services.Remove(original);
            services.AddSingleton<IAgentConnections>(sp => new WithDisks((IAgentConnections)original.ImplementationFactory!(sp), Disks));
        });
    }

    private sealed class WithDisks(IAgentConnections inner, ConcurrentDictionary<NodeId, IReadOnlyList<DiskUsage>> disks) : IAgentConnections
    {
        public AgentConnectionInfo? Find(NodeId node)
        {
            var info = inner.Find(node);
            return info is not null && disks.TryGetValue(node, out var set)
                ? info with { LastMetrics = (info.LastMetrics ?? new NodeMetrics(DateTimeOffset.UtcNow, 0, 0, 0, TimeSpan.Zero)) with { Disks = set } }
                : info;
        }

        public void Disconnect(NodeId node) => inner.Disconnect(node);

        public void DisconnectEnded(NodeId node) => inner.DisconnectEnded(node);

        public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

        public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
            inner.SendAsync(node, command, actor, cancellationToken, operationId);
    }
}
