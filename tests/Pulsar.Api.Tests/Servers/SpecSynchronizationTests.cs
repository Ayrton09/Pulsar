using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Servers;

public sealed class SpecSynchronizationTests(WatchedCommandsApiFactory factory) : IClassFixture<WatchedCommandsApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ASyncWaitsForASameTemplateReinstallAndReadsItsResetSettings()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "spec-turn");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        await GameServerTests.AllocateAsync(admin, node.Node.Id, "0.0.0.0", "27015-27020");
        var id = await GameServerTests.InstalledAsync(admin, agent, "css");
        using (var settings = await admin.PutAsync($"/api/v1/servers/{id}/settings",
                   new UpdateServerSettingsRequest(new() { ["map"] = "cs_office" }, [], false, false)))
        {
            await AccessScenario.EnsureSuccessAsync(settings);
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.BeforeSend = async (command, _) =>
        {
            if (command is ProvisionServerCommand { ServerId: var server } && server == id)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Ct);
            }
        };
        try
        {
            var details = await ServerApi.GetAsync(admin, id);
            var reinstall = admin.PostAsync($"/api/v1/servers/{id}/game", new ChangeGameRequest(details!.Server.TemplateId));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await using var scope = factory.Services.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<ServerManagement>().SyncSpecAsync(new ServerId(id), Ct);
            Assert.False(sync.IsCompleted);
            release.TrySetResult();
            using var changed = await reinstall;
            await AccessScenario.EnsureSuccessAsync(changed);
            Assert.True((await sync).IsSuccess);
            Assert.False(agent.StoredSpec(id).Variables.ContainsKey("map"));
        }
        finally
        {
            release.TrySetResult();
            factory.BeforeSend = null;
        }
    }

    [DatabaseFact]
    public async Task ATrackedOldAllocationNeverReturnsBesideTheNewAssignment()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "fresh-spec-ports");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        await GameServerTests.AllocateAsync(admin, node.Node.Id, "0.0.0.0", "27015-27020");
        var id = await GameServerTests.InstalledAsync(admin, agent, "css");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var server = await db.Servers.Include(s => s.Variables).SingleAsync(s => s.Id == new ServerId(id), Ct);
        var old = await db.NodeAllocations.SingleAsync(a => a.ServerId == server.Id && a.PortName == "game", Ct);
        var other = await db.NodeAllocations.AsNoTracking().FirstAsync(a => a.NodeId == server.NodeId && a.ServerId == null, Ct);
        using (var changed = await admin.PutAsync($"/api/v1/servers/{id}/ports", new UpdateServerPortsRequest(new() { ["game"] = other.Id.Value })))
        {
            await AccessScenario.EnsureSuccessAsync(changed);
        }

        // This context still tracks the old, unchanged row as assigned. It must not enter the saved spec.
        Assert.Equal(server.Id, old.ServerId);
        var specs = scope.ServiceProvider.GetRequiredService<ServerSpecs>();
        Assert.Equal(other.Id, Assert.Single(await specs.AllocationsOf(server, Ct)).Id);
        Assert.True((await scope.ServiceProvider.GetRequiredService<ServerManagement>().SyncSpecAsync(server.Id, Ct)).IsSuccess);
        Assert.Equal(other.Port, agent.StoredSpec(id).Ports!["game"].Port);

        // A game change builds before it saves: unsaved releases and assignments still take precedence over the database.
        var current = await db.NodeAllocations.SingleAsync(a => a.Id == other.Id, Ct);
        current.Release();
        old.Assign(server.Id, "game");
        db.Entry(old).Property(a => a.ServerId).IsModified = true;
        Assert.Equal(old.Id, Assert.Single(await specs.AllocationsOf(server, Ct)).Id);
    }
}
