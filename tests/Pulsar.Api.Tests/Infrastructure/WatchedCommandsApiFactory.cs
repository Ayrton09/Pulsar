using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>The panel, calling <see cref="BeforeSend"/> with each command to an agent just before it goes out.</summary>
public sealed class WatchedCommandsApiFactory : PulsarApiFactory
{
    public Func<AgentCommand, Guid?, Task>? BeforeSend { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
            services.Remove(original);
            services.AddSingleton<IAgentConnections>(sp => new Watched((IAgentConnections)original.ImplementationFactory!(sp), this));
        });
    }

    private sealed class Watched(IAgentConnections inner, WatchedCommandsApiFactory factory) : IAgentConnections
    {
        public AgentConnectionInfo? Find(NodeId node) => inner.Find(node);

        public void Disconnect(NodeId node) => inner.Disconnect(node);

        public void DisconnectEnded(NodeId node) => inner.DisconnectEnded(node);

        public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

        public async Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken,
            Guid? operationId = null)
        {
            if (factory.BeforeSend is { } watch)
            {
                await watch(command, operationId);
            }

            return await inner.SendAsync(node, command, actor, cancellationToken, operationId);
        }
    }
}
