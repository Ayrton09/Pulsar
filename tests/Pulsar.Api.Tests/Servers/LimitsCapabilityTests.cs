using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
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
    public sealed class WithoutLimitsFactory() : WithoutCapabilityFactory(AgentCapabilities.Limits);
}
