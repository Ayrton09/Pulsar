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
/// Cloning a server (Pulsar 1.0.16): a new server of the same game and values on a node of the user's choosing, its
/// files a copy of the source's, from a backup the source's node sends to the panel and the panel hands to the clone's
/// node as it arrives; the source is left as it was.
/// </summary>
public sealed class ServerCloneTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AServerIsClonedOntoAnotherNodeWithItsFilesAndValues()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var first = await ConnectedAgentAsync(admin, "clone-from");
        await using var second = await ConnectedAgentAsync(admin, "clone-to");
        var created = await ServerApi.CreateAsync(admin, first.Config!.NodeId, "Original");
        var source = created.Server.Id;
        await ServerApi.WaitForAsync(admin, source, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        var config = Path.Combine(first.ServersRoot, created.Server.ShortId, "data", "server.cfg");
        await File.WriteAllTextAsync(config, "hostname the original's", Ct);

        // Its mods, addons, pack and Steam branch go along, and what the node recorded of them.
        var record = Path.Combine(first.ServersRoot, created.Server.ShortId, ".pulsar", "addons", "oxide.json");
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        await File.WriteAllTextAsync(record, "{}", Ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var now = DateTimeOffset.UtcNow;
            var row = await db.Servers.SingleAsync(s => s.Id == new ServerId(source), Ct);
            row.RestoreSteamBranch("beta", now);
            var mod = ServerMod.Installing(row.Id, ModSource.Modrinth, "lithium", "lithium1", "Lithium", "1.0", "mods/lithium.jar", "ab", 2, false, [],
                new OperationId(Guid.NewGuid()), now);
            mod.Installed(now);
            db.ServerMods.Add(mod);
            db.ServerAddons.Add(ServerAddon.Installed(row.Id, "oxide", "2.0.1", now));
            await db.SaveChangesAsync(Ct);
        }

        // Making a backup of the source is part of it.
        // Its files become readable on the clone: backing it up is not enough without seeing them.
        using (var creator = await scenario.SignedInWithAsync("creator", Permissions.Server.View, Permissions.Server.Create, Permissions.Server.BackupsCreate))
        using (var refused = await creator.PostAsync($"/api/v1/servers/{source}/clone", new CloneServerRequest(second.Config!.NodeId, "Copy")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // Its files are copied whole: never while it runs (the game holds some open).
        await ServerApi.CommandAsync(admin, source, "start");
        await ServerApi.WaitForAsync(admin, source, s => s.Server.RuntimeState == ServerRuntimeState.Running, "the source running");
        using (var running = await admin.PostAsync($"/api/v1/servers/{source}/clone", new CloneServerRequest(second.Config!.NodeId, "Copy")))
        {
            Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);
        }

        await ServerApi.CommandAsync(admin, source, "stop");
        await ServerApi.WaitForAsync(admin, source, s => s.Server.RuntimeState == ServerRuntimeState.Offline && s.Server.ActiveOperation is null, "the source stopped");

        ServerDetails clone;
        using (var cloned = await admin.PostAsync($"/api/v1/servers/{source}/clone", new CloneServerRequest(second.Config!.NodeId, "Copy")))
        {
            await AccessScenario.EnsureSuccessAsync(cloned);
            clone = (await cloned.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
        }

        Assert.Equal((second.Config.NodeId, created.Server.TemplateId), (clone.Server.NodeId, clone.Server.TemplateId));

        // One copy at a time from a server (each holds a transfer place on both nodes).
        using (var again = await admin.PostAsync($"/api/v1/servers/{source}/clone", new CloneServerRequest(first.Config.NodeId, "Copy 2")))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
        Assert.NotEqual(created.Server.ShortId, clone.Server.ShortId);

        // Installed, then the source's files restored into it.
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await SequenceDriver.DriveAsync(factory);
            return await SequenceAsync(clone.Server.Id) is { Status: not SequenceStatus.Running };
        }, "the copy ended");
        var copy = await SequenceAsync(clone.Server.Id);
        Assert.True(copy!.Status == SequenceStatus.Succeeded, $"{copy.Status} {copy.ErrorCode} {copy.Message}");
        var copied = Path.Combine(second.ServersRoot, clone.Server.ShortId, "data", "server.cfg");
        Assert.Equal("hostname the original's", await File.ReadAllTextAsync(copied, Ct));
        Assert.Equal(new ServerCopyView(source, "Original", SequenceStatus.Succeeded, null, null), (await ServerApi.GetAsync(admin, clone.Server.Id))!.Copy);
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(second.ServersRoot, clone.Server.ShortId, ".pulsar", "addons", "oxide.json"), Ct));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var cloneId = new ServerId(clone.Server.Id);
            Assert.Equal("beta", (await db.Servers.AsNoTracking().SingleAsync(s => s.Id == cloneId, Ct)).SteamBranch);
            var mod = await db.ServerMods.AsNoTracking().SingleAsync(m => m.ServerId == cloneId, Ct);
            Assert.Equal(("lithium1", "mods/lithium.jar", ServerModState.Installed), (mod.VersionId, mod.Path, mod.State));
            Assert.Equal("2.0.1", (await db.ServerAddons.AsNoTracking().SingleAsync(a => a.ServerId == cloneId, Ct)).Release);
        }


        // The archive is now a backup of the clone too, and the source is as it was.
        var backups = await admin.Http.GetFromJsonAsync<BackupList>(new Uri($"/api/v1/servers/{clone.Server.Id}/backups", UriKind.Relative), AccessScenario.Json, Ct);
        var received = Assert.Single(backups!.Backups);
        Assert.Equal(BackupStatus.Completed, received.Status);
        Assert.True(File.Exists(Path.Combine(second.Configs.Paths.BackupsDirectory, clone.Server.ShortId, $"{received.Id:N}.zip")));
        Assert.Equal("hostname the original's", await File.ReadAllTextAsync(config, Ct));
        var original = (await ServerApi.GetAsync(admin, source))!;
        Assert.Equal((InstallState.Installed, ServerRuntimeState.Offline), (original.Server.InstallState, original.Server.RuntimeState));

        // It runs.
        await ServerApi.CommandAsync(admin, clone.Server.Id, "start");
        await ServerApi.WaitForAsync(admin, clone.Server.Id, s => s.Server.RuntimeState == ServerRuntimeState.Running, "the clone running");
        await ServerApi.CommandAsync(admin, clone.Server.Id, "stop");
    }

    private async Task<ServerSequence?> SequenceAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ServerSequences.AsNoTracking()
            .Where(s => s.ServerId == new ServerId(server) && s.Origin == SequenceOrigin.Clone).SingleOrDefaultAsync(Ct);
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
