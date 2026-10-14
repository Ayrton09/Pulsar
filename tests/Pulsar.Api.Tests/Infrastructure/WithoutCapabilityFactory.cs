using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The panel, seeing its agents without <paramref name="capability"/>: as it sees an older agent that does not announce
/// it. The in-process agent runs the current core, which announces everything, and still runs what the panel sends.
/// </summary>
public abstract class WithoutCapabilityFactory(string capability) : PulsarApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
            services.Remove(original);
            services.AddSingleton<IAgentConnections>(sp => new Without((IAgentConnections)original.ImplementationFactory!(sp), capability));
        });
    }

    private sealed class Without(IAgentConnections inner, string capability) : IAgentConnections
    {
        public AgentConnectionInfo? Find(NodeId node) =>
            inner.Find(node) is { } info ? info with { Capabilities = [.. info.Capabilities.Where(c => c != capability)] } : null;

        public void Disconnect(NodeId node) => inner.Disconnect(node);

        public void DisconnectEnded(NodeId node) => inner.DisconnectEnded(node);

        public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

        public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
            inner.SendAsync(node, command, actor, cancellationToken, operationId);
    }
}
