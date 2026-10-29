using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Moving a server to another node (Pulsar 1.0.16), as the same server: backed up on its node, installed on the other,
/// the backup restored there (relayed by the panel), then its old copy deleted with that node's allocations and
/// backups; a move that fails before then leaves it on its old node as it was.
/// </summary>
public sealed class ServerTransferTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AServerMovesToAnotherNodeAsTheSameServerWithItsFiles()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var first = await ConnectedAgentAsync(admin, "transfer-from");
        await using var second = await ConnectedAgentAsync(admin, "transfer-to");
        await GameServerTests.AllocateAsync(admin, first.Config!.NodeId, "0.0.0.0", "27015-27020");
        await GameServerTests.AllocateAsync(admin, second.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await GameServerTests.InstalledAsync(admin, first, "css");
        var created = (await ServerApi.GetAsync(admin, server))!;
        var oldFolder = Path.Combine(first.ServersRoot, created.Server.ShortId);
        var config = Path.Combine("cstrike", "cfg", "server.cfg");
        Directory.CreateDirectory(Path.Combine(oldFolder, "cstrike", "cfg"));
        await File.WriteAllTextAsync(Path.Combine(oldFolder, config), "hostname \"moving along\"", Ct);
        // A file no backup rule of the game names travels too: the old copy is deleted.
        var outside = Path.Combine("cstrike", "plugins", "outside.dll");
        Directory.CreateDirectory(Path.Combine(oldFolder, "cstrike", "plugins"));
        await File.WriteAllTextAsync(Path.Combine(oldFolder, outside), "not in the rules", Ct);
        var oldPorts = await AllocationsAsync(server, first.Config.NodeId);
        Assert.NotEmpty(oldPorts);

        // The old copy is deleted: installing it is not enough.
        using (var installer = await scenario.SignedInWithAsync("installer", Permissions.Server.View, Permissions.Server.Install,
                   Permissions.Server.Create, Permissions.Server.BackupsCreate))
        using (var refused = await installer.PostAsync($"/api/v1/servers/{server}/transfer", new TransferServerRequest(second.Config!.NodeId)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var moving = await admin.PostAsync($"/api/v1/servers/{server}/transfer", new TransferServerRequest(second.Config!.NodeId)))
        {
            await AccessScenario.EnsureSuccessAsync(moving);
        }

        // Nothing else happens to it meanwhile.
        using (var start = await admin.PostAsync($"/api/v1/servers/{server}/start"))
        {
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        }

        var transfer = await EndedAsync(server);
        Assert.True(transfer.Status == SequenceStatus.Succeeded, $"{transfer.Status} {transfer.ErrorCode} {transfer.Message}");
        var moved = (await ServerApi.GetAsync(admin, server))!;
        Assert.Equal((second.Config.NodeId, created.Server.ShortId, created.Server.Name), (moved.Server.NodeId, moved.Server.ShortId, moved.Server.Name));
        Assert.Contains("moving along", await File.ReadAllTextAsync(Path.Combine(second.ServersRoot, created.Server.ShortId, config), Ct), StringComparison.Ordinal);
        Assert.Equal("not in the rules", await File.ReadAllTextAsync(Path.Combine(second.ServersRoot, created.Server.ShortId, outside), Ct));

        // Its old copy is gone, with that node's allocations and backups; the backup that came along is the new node's.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(!Directory.Exists(oldFolder)), "the old copy deleted");
        Assert.Empty(await AllocationsAsync(server, first.Config.NodeId));
        Assert.NotEmpty(await AllocationsAsync(server, second.Config.NodeId));
        var backups = await admin.Http.GetFromJsonAsync<BackupList>(new Uri($"/api/v1/servers/{server}/backups", UriKind.Relative), AccessScenario.Json, Ct);
        Assert.Equal(BackupStatus.Completed, Assert.Single(backups!.Backups).Status);

        // It runs on its new node.
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running on its new node");
        Assert.Contains(second.Processes.Started, p => !p.IsSteamCmd);
        await ServerApi.CommandAsync(admin, server, "stop");
    }

    [DatabaseFact]
    public async Task AMoveThatFailsLeavesTheServerOnItsOldNodeAsItWas()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var first = await ConnectedAgentAsync(admin, "transfer-stays");
        var second = await ConnectedAgentAsync(admin, "transfer-goes-away");
        await GameServerTests.AllocateAsync(admin, first.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await GameServerTests.InstalledAsync(admin, first, "css");

        // A node without a free allocation for its ports is refused at once, before anything is backed up.
        using (var full = await admin.PostAsync($"/api/v1/servers/{server}/transfer", new TransferServerRequest(second.Config!.NodeId)))
        {
            Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        }

        Assert.Equal(OperationType.ServerInstall, (await ServerApi.GetAsync(admin, server))!.RecentOperations[0].Type);
        await GameServerTests.AllocateAsync(admin, second.Config!.NodeId, "0.0.0.0", "27015-27020");
        var oldPorts = await AllocationsAsync(server, first.Config.NodeId);
        Assert.NotEmpty(oldPorts);

        using (var moving = await admin.PostAsync($"/api/v1/servers/{server}/transfer", new TransferServerRequest(second.Config!.NodeId)))
        {
            await AccessScenario.EnsureSuccessAsync(moving);
        }

        // The new node goes away before the server is installed there.
        await second.DisposeAsync();
        var transfer = await EndedAsync(server);
        Assert.Equal(SequenceStatus.Failed, transfer.Status);

        var back = (await ServerApi.GetAsync(admin, server))!;
        Assert.Equal((first.Config.NodeId, InstallState.Installed, ServerRuntimeState.Offline), (back.Server.NodeId, back.Server.InstallState, back.Server.RuntimeState));
        Assert.Equal(oldPorts, await AllocationsAsync(server, first.Config.NodeId));
        Assert.Empty(await AllocationsAsync(server, second.Config.NodeId));
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running on its old node");
        await ServerApi.CommandAsync(admin, server, "stop");
    }

    private async Task<ServerSequence> EndedAsync(Guid server)
    {
        ServerSequence? transfer = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await SequenceDriver.DriveAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            transfer = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ServerSequences.AsNoTracking()
                .SingleOrDefaultAsync(s => s.ServerId == new ServerId(server) && s.Origin == SequenceOrigin.Transfer, Ct);
            return transfer is { Status: not SequenceStatus.Running };
        }, () => $"the transfer ended (last: {transfer?.Status} {transfer?.ErrorCode})");
        return transfer!;
    }

    private async Task<List<int>> AllocationsAsync(Guid server, Guid node)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().NodeAllocations.AsNoTracking()
            .Where(a => a.ServerId == new ServerId(server) && a.NodeId == new NodeId(node)).OrderBy(a => a.Port).Select(a => a.Port)
            .ToListAsync(Ct);
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, name);
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
