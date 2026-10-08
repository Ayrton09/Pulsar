using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Limits set while the node was away never let a game start uncapped on an agent that cannot enforce them.</summary>
public sealed class LimitsCapabilityTests(LimitsCapabilityTests.WithoutLimitsFactory factory) : IClassFixture<LimitsCapabilityTests.WithoutLimitsFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AServerWithLimitsDoesNotStartOnAnAgentWithoutThem()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "no-limits");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        var server = (await ServerApi.CreateAsync(admin, node.Node.Id, "Capped")).Server.Id;
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");

        // As if set while the node was disconnected (then the panel cannot ask the agent what it can do).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var row = await db.Servers.SingleAsync(s => s.Id == new ServerId(server), Ct);
            Assert.True(row.SetLimits(50, null, DateTimeOffset.UtcNow).IsSuccess);
            await db.SaveChangesAsync(Ct);
        }

        foreach (var command in new[] { "start", "restart" })
        {
            using var response = await admin.PostAsync($"/api/v1/servers/{server}/{command}");
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{command}: {(int)response.StatusCode} {body}");
            Assert.Contains("server.limits_unsupported", body, StringComparison.Ordinal);
        }

        Assert.Equal(ServerRuntimeState.Offline, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);
    }

    /// <summary>The panel, seeing its agents without the limits capability.</summary>
    public sealed class WithoutLimitsFactory : PulsarApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
                services.Remove(original);
                services.AddSingleton<IAgentConnections>(sp => new WithoutLimits((IAgentConnections)original.ImplementationFactory!(sp)));
            });
        }

        private sealed class WithoutLimits(IAgentConnections inner) : IAgentConnections
        {
            public AgentConnectionInfo? Find(NodeId node) =>
                inner.Find(node) is { } info ? info with { Capabilities = [.. info.Capabilities.Where(c => c != AgentCapabilities.Limits)] } : null;

            public void Disconnect(NodeId node) => inner.Disconnect(node);

            public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

            public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
                inner.SendAsync(node, command, actor, cancellationToken, operationId);
        }
    }
}
