using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>An agent that can install addons but not remove them is refused the removal, not sent it.</summary>
public sealed class AddonCapabilityTests(AddonCapabilityTests.WithoutRemoveFactory factory) : IClassFixture<AddonCapabilityTests.WithoutRemoveFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task RemovingNeedsTheRemoveCapability()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "no-addon-removal");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "28015-28030");
        var template = await GameServerTests.TemplateAsync(admin, "rust");
        Guid server;
        using (var create = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node.Node.Id, template.Template.Id, "rust", null)))
        {
            await AccessScenario.EnsureSuccessAsync(create);
            server = (await create.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        }

        await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null && s.Server.InstallState == InstallState.Installed, "installed");

        using var response = await admin.DeleteAsync($"/api/v1/servers/{server}/addons/oxide");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("addon.unsupported", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>The panel, seeing its agents without the remove-addon capability.</summary>
    public sealed class WithoutRemoveFactory : PulsarApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
                services.Remove(original);
                services.AddSingleton<IAgentConnections>(sp => new WithoutRemove((IAgentConnections)original.ImplementationFactory!(sp)));
            });
        }

        private sealed class WithoutRemove(IAgentConnections inner) : IAgentConnections
        {
            public AgentConnectionInfo? Find(NodeId node) =>
                inner.Find(node) is { } info ? info with { Capabilities = [.. info.Capabilities.Where(c => c != AgentCapabilities.RemoveAddon)] } : null;

            public void Disconnect(NodeId node) => inner.Disconnect(node);

            public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

            public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
                inner.SendAsync(node, command, actor, cancellationToken, operationId);
        }
    }
}
