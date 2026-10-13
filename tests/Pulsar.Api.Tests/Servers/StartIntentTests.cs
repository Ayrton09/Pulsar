using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>A start the node took stays wanted, whatever the server's row says by the time it is saved.</summary>
public sealed class StartIntentTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AStartTheNodeTookIsWantedEvenIfTheRowChangedSoThatItWouldBeRefusedNow()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "start-intent");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        var created = await ServerApi.CreateAsync(admin, node.Node.Id, "Start intent");
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        // The node takes the start; meanwhile the row changes so that the same request would be refused (not installed).
        var connections = new InterceptedConnections(services.GetRequiredService<IAgentConnections>(), async (command, next, token) =>
        {
            if (command is StartServerCommand)
            {
                await using var other = factory.Services.CreateAsyncScope();
                await other.ServiceProvider.GetRequiredService<IPulsarDbContext>().Database
                    .ExecuteSqlAsync($"UPDATE servers SET install_state = 'failed' WHERE id = {created.Server.Id}", token);
                return new CommandAck(Guid.CreateVersion7(), AckStatus.Accepted, null, null);
            }

            return await next(token);
        });
        var servers = ActivatorUtilities.CreateInstance<ServerManagement>(services, (IAgentConnections)connections);

        Assert.Equal(AckStatus.Accepted, (await servers.StartAsync(new ServerId(created.Server.Id), Ct)).Value.Status);

        Assert.Equal(DesiredState.Running, (await ServerApi.GetAsync(admin, created.Server.Id))!.Server.DesiredState);
    }
}
