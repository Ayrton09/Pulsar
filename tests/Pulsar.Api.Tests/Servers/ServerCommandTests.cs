using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// What the panel records about commands it sends: operations sent whatever their request does meanwhile, installs that
/// can no longer run, and starts the node refuses.
/// </summary>
public sealed class ServerCommandTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AnOperationIsSentAndRecordedEvenWhenItsRequestIsGivenUp()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "given-up");
        var id = await InstalledServerAsync(admin, agent, "Given up");
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        Exception? failure = null;
        var connections = new InterceptedConnections(services.GetRequiredService<IAgentConnections>(), async (command, next, token) =>
        {
            if (failure is not null)
            {
                throw failure;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), token); // the agent's ack is on its way
            return await next(token);
        });
        var operations = ActivatorUtilities.CreateInstance<ServerOperations>(services, (IAgentConnections)connections);
        var db = services.GetRequiredService<IPulsarDbContext>();

        // The request goes away while the ack is awaited: the command still goes, and its operation is the node's.
        var server = await db.Servers.SingleAsync(s => s.Id == new ServerId(id), Ct);
        using (var givenUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var run = await operations.RunAsync(server, Backup(server), new CreateBackupCommand(id, Guid.CreateVersion7()), null, givenUp.Token);
            Assert.NotEqual(OperationStatus.Queued, run.Value.Status);
        }

        var done = await ServerApi.WaitForAsync(admin, id, s => s.Server.ActiveOperation is null, "backup made");
        Assert.Equal(OperationStatus.Succeeded, done.RecentOperations.First(o => o.Type == OperationType.BackupCreate).Status);

        // A send that fails fails its operation, and the server is free again.
        failure = new InvalidOperationException("No key to sign with.");
        db.ChangeTracker.Clear();
        server = await db.Servers.SingleAsync(s => s.Id == new ServerId(id), Ct);
        var failing = Backup(server);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.RunAsync(server, failing, new CreateBackupCommand(id, Guid.CreateVersion7()), null, Ct));
        var details = await ServerApi.GetAsync(admin, id);
        Assert.Null(details!.Server.ActiveOperation);
        Assert.Equal(OperationStatus.Failed, details.RecentOperations.Single(o => o.Id == failing.Id.Value).Status);
    }

    [DatabaseFact]
    public async Task AnInstallThatCanNoLongerRunFails()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "lost-install");
        var stuck = await InstalledServerAsync(admin, agent, "Lost install");
        var refused = await InstalledServerAsync(admin, agent, "Refused install");

        // Provisioned, but the install that follows was lost (the panel restarted in between): the agent's next Hello says so.
        await SetAsync(stuck, "UPDATE servers SET install_state = 'installing' WHERE id = {0}");
        await agent.StopAsync();
        await agent.StartAsync();
        await ServerApi.WaitForAsync(admin, stuck, s => s.Server.InstallState == InstallState.Failed, "install failed at reconciliation");

        // Provisioned, but the install cannot begin (the game was started meanwhile): it failed.
        await SetAsync(refused, "UPDATE servers SET install_state = 'installing', runtime_state = 'running' WHERE id = {0}");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ServerManagement>().ContinueInstallAsync(new ServerId(refused), null, Ct);
        }

        Assert.Equal(InstallState.Failed, (await ServerApi.GetAsync(admin, refused))!.Server.InstallState);
    }

    [DatabaseFact]
    public async Task AStartTheNodeRefusesIsNeitherWantedNorAuditedAsDone()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "refused-start");
        var id = await InstalledServerAsync(admin, agent, "Refused start");
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var connections = new InterceptedConnections(services.GetRequiredService<IAgentConnections>(), (command, next, token) =>
            command is StartServerCommand
                ? Task.FromResult<CommandAck?>(new CommandAck(Guid.CreateVersion7(), AckStatus.Busy, "server.busy", "Another start is under way."))
                : next(token));
        var servers = ActivatorUtilities.CreateInstance<ServerManagement>(services, (IAgentConnections)connections);

        var started = await servers.StartAsync(new ServerId(id), Ct);
        Assert.Equal(AckStatus.Busy, started.Value.Status);

        Assert.Equal(DesiredState.Stopped, (await ServerApi.GetAsync(admin, id))!.Server.DesiredState);
        var db = services.GetRequiredService<IPulsarDbContext>();
        var entry = await db.AuditLog.AsNoTracking().SingleAsync(e => e.ServerId == new ServerId(id) && e.Action == AuditActions.Server.Started, Ct);
        Assert.Equal(AuditOutcome.Failure, entry.Outcome);
        Assert.Contains("server.busy", entry.Metadata, StringComparison.Ordinal);
    }

    private static Operation Backup(Server server) =>
        Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, DateTimeOffset.UtcNow);

    private async Task SetAsync(Guid server, string sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Database.ExecuteSqlRawAsync(sql, [server], Ct);
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"commands-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private static async Task<Guid> InstalledServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }
}
