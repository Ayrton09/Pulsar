using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Nodes;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Phase 4 through the panel and the real agent core: a Source game (the bundled Counter-Strike: Source template) with
/// ports from the node's allocations, variables (a secret one too), a SteamCMD install with progress, its configuration
/// file, and readiness and stop over RCON. SteamCMD and the game are scripted processes.
/// </summary>
public sealed class GameServerTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ASourceGameIsCreatedInstalledConfiguredStartedAndStopped()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "css");
        var nodeId = agent.Config!.NodeId;
        await AllocateAsync(admin, nodeId, "0.0.0.0", "27015-27020");
        var template = await TemplateAsync(admin, "css");

        var created = await CreateAsync(admin, new CreateServerRequest(nodeId, template.Template.Id, "My CS:S", null,
            new Dictionary<string, string?> { ["map"] = "cs_office", ["password"] = "join-me" }));

        // The port with the game's default, a CSTV port only when asked for.
        Assert.Equal([("game", 27015), ("tv", (int?)null)], created.Ports.Select(p => (p.Name, p.Port)));
        var installed = await ServerApi.WaitForAsync(admin, created.Server.Id,
            s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed with SteamCMD");
        Assert.Equal([OperationType.ServerInstall, OperationType.ServerProvision], installed.RecentOperations.Select(o => o.Type));
        Assert.Equal(1, agent.Processes.SteamCmdRuns);
        var root = Path.Combine(agent.ServersRoot, created.Server.ShortId);
        Assert.True(File.Exists(Path.Combine(root, ".pulsar", "steamcmd", "steamcmd.exe")));
        Assert.True(File.Exists(Path.Combine(root, "srcds.exe")));

        // The secret is stored encrypted and never returned.
        var settings = await SettingsAsync(admin, created.Server.Id);
        var password = settings.Variables.Single(v => v.Definition.Key == "password");
        Assert.True(password.HasValue);
        Assert.Null(password.Value);
        Assert.Equal("cs_office", settings.Variables.Single(v => v.Definition.Key == "map").Value);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var stored = await db.Servers.AsNoTracking().Include(s => s.Variables).SingleAsync(s => s.Id == new ServerId(created.Server.Id), Ct);
            var secret = stored.Variables.Single(v => v.Key == "password");
            Assert.Null(secret.Value);
            Assert.DoesNotContain("join-me", System.Text.Encoding.UTF8.GetString(secret.ProtectedValue!), StringComparison.Ordinal);
        }

        Assert.Equal(AckStatus.Accepted, (await ServerApi.CommandAsync(admin, created.Server.Id, "start")).Status);
        var running = await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.RuntimeState == ServerRuntimeState.Running, "ready over RCON");
        Assert.Equal(ServerStateReason.Ready, running.Server.LastStateReason);
        var game = agent.Processes.Started.Last(p => !p.IsSteamCmd);
        Assert.Equal(["+map", "cs_office"], game.Arguments.SkipWhile(a => a != "+map").Take(2));
        Assert.Equal(27015, game.GamePort);
        var config = await File.ReadAllTextAsync(Path.Combine(root, "cstrike", "cfg", "server.cfg"), Ct);
        Assert.Contains("hostname \"Pulsar CS:S\"", config, StringComparison.Ordinal);
        Assert.Contains("sv_password \"join-me\"", config, StringComparison.Ordinal);

        await ServerApi.CommandAsync(admin, created.Server.Id, "stop");
        var stopped = await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped over RCON");
        Assert.Equal(0, stopped.Server.LastExitCode);
        Assert.Contains("quit", agent.Rcon.Commands);
    }

    [DatabaseFact]
    public async Task SettingsChangedInThePanelApplyAtTheNextStart()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "settings");
        await AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await InstalledAsync(admin, agent, "css");

        var updated = await PutAsync<ServerSettings>(admin, $"/api/v1/servers/{server}/settings", new UpdateServerSettingsRequest(
            new Dictionary<string, string?> { ["hostname"] = "Renamed", ["maxPlayers"] = "32", ["password"] = "" }, ["-tickrate", "66"], AutoStart: true,
            RestartOnCrash: false));
        Assert.Equal(["-tickrate", "66"], updated.ExtraArguments);
        Assert.False(updated.Variables.Single(v => v.Definition.Key == "password").HasValue);

        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        var game = agent.Processes.Started.Last(p => !p.IsSteamCmd);
        Assert.Equal(["+maxplayers", "32"], game.Arguments.SkipWhile(a => a != "+maxplayers").Take(2));
        Assert.Equal(["-tickrate", "66"], game.Arguments.TakeLast(2));
        var config = await File.ReadAllTextAsync(Path.Combine(agent.ServersRoot, (await ServerApi.GetAsync(admin, server))!.Server.ShortId, "cstrike",
            "cfg", "server.cfg"), Ct);
        Assert.Contains("hostname \"Renamed\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("sv_password", config, StringComparison.Ordinal);

        // Ports change only while stopped.
        using (var refused = await PutResponseAsync(admin, $"/api/v1/servers/{server}/ports",
                   new UpdateServerPortsRequest(new Dictionary<string, Guid?> { ["tv"] = null })))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }
    }

    [DatabaseTheory]
    [InlineData("map", "de_dust2 +rcon_password x", "server.invalid_settings")]
    [InlineData("maxPlayers", "500", "server.invalid_settings")]
    [InlineData("nope", "1", "server.invalid_settings")]
    public async Task ValuesTheManifestRefusesAreRefused(string key, string value, string code)
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, $"refuse-{key}");
        await AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015");
        var template = await TemplateAsync(admin, "css");

        using var response = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config.NodeId, template.Template.Id, "Bad", null,
            new Dictionary<string, string?> { [key] = value }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task AServerNeedsAFreeAllocationForEachRequiredPort()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "allocations");
        var template = await TemplateAsync(admin, "css");

        using (var none = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config!.NodeId, template.Template.Id, "No ports", null)))
        {
            Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
            Assert.Contains("server.no_free_allocation", await none.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        var allocations = await AllocateAsync(admin, agent.Config.NodeId, "0.0.0.0", "27100");
        var first = await CreateAsync(admin, new CreateServerRequest(agent.Config.NodeId, template.Template.Id, "First", null));
        Assert.Equal(27100, first.Ports.Single(p => p.Name == "game").Port);

        using (var second = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config.NodeId, template.Template.Id, "Second", null,
                   Ports: new Dictionary<string, Guid?> { ["game"] = allocations[0].Id })))
        {
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        using var deleteAssigned = await admin.Http.DeleteAsync(
            new Uri($"/api/v1/nodes/{agent.Config.NodeId}/allocations/{allocations[0].Id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Conflict, deleteAssigned.StatusCode);
    }

    [DatabaseFact]
    public async Task APortThatFollowsAnotherGetsTheNextAllocation()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "following");
        var template = await TemplateAsync(admin, "valheim");
        var nodeId = agent.Config!.NodeId;
        var password = new Dictionary<string, string?> { ["password"] = "secret123" };
        // 2456-2459 in pairs, and 2470 alone (its next port is not an allocation).
        await AllocateAsync(admin, nodeId, "0.0.0.0", "2456-2459,2470");

        var first = await CreateAsync(admin, new CreateServerRequest(nodeId, template.Template.Id, "First", null, Variables: password));
        Assert.Equal((2456, 2457), (Port(first, "game"), Port(first, "query")));
        var second = await CreateAsync(admin, new CreateServerRequest(nodeId, template.Template.Id, "Second", null, Variables: password));
        Assert.Equal((2458, 2459), (Port(second, "game"), Port(second, "query")));

        // 2470 is free, but 2471 is not an allocation: no third server.
        using var third = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(nodeId, template.Template.Id, "Third", null,
            Variables: password));
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Contains("server.no_free_allocation", await third.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task AllocationsNeverOverlap()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "overlaps");

        await AllocateAsync(admin, node.Node.Id, "0.0.0.0", "27015-27016");
        foreach (var (address, ports) in new[] { ("192.168.1.10", "27016"), ("0.0.0.0", "27014-27015") })
        {
            using var overlap = await admin.PostAsync($"/api/v1/nodes/{node.Node.Id}/allocations", new CreateAllocationsRequest(address, ports, null));
            Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        }

        var other = await AllocateAsync(admin, node.Node.Id, "192.168.1.10", "27017, 27019-27020");
        Assert.Equal([27017, 27019, 27020], other.Select(a => a.Port));
        foreach (var bad in new[] { "0", "70000", "27030-27020", "a-b", "1-2000" })
        {
            using var refused = await admin.PostAsync($"/api/v1/nodes/{node.Node.Id}/allocations", new CreateAllocationsRequest("10.0.0.1", bad, null));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using var notIpv4 = await admin.PostAsync($"/api/v1/nodes/{node.Node.Id}/allocations", new CreateAllocationsRequest("::1", "27015", null));
        Assert.Equal(HttpStatusCode.BadRequest, notIpv4.StatusCode);
        using var deleted = await admin.Http.DeleteAsync(new Uri($"/api/v1/nodes/{node.Node.Id}/allocations/{other[0].Id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [DatabaseFact]
    public async Task AnUpdateCanBeCancelledAndDeletingTheServerFreesItsPorts()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "update");
        await AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await InstalledAsync(admin, agent, "css");

        agent.Processes.SteamCmdGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var update = await admin.PostAsync($"/api/v1/servers/{server}/update", new UpdateGameRequest(Validate: true)))
        {
            await AccessScenario.EnsureSuccessAsync(update);
        }

        // Progress shows up live (from the panel's memory, not only the database).
        var downloading = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is { Stage: "downloading", Percent: 45 },
            "downloading");
        var operation = downloading.Server.ActiveOperation!;
        Assert.Equal(OperationType.ServerUpdate, operation.Type);
        Assert.True(operation.Cancellable);
        Assert.Contains("validate", agent.Processes.Started.Last(p => p.IsSteamCmd).Arguments);

        using (var start = await admin.PostAsync($"/api/v1/servers/{server}/start"))
        {
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode); // busy
        }

        using (var cancel = await admin.PostAsync($"/api/v1/servers/{server}/operations/{operation.Id}/cancel"))
        {
            await AccessScenario.EnsureSuccessAsync(cancel);
        }

        var cancelled = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "update cancelled");
        Assert.Equal(OperationStatus.Cancelled, cancelled.RecentOperations[0].Status);
        Assert.Equal(InstallState.Installed, cancelled.Server.InstallState);
        Assert.All(agent.Processes.Started, p => Assert.False(p.IsAlive));

        using (var delete = await admin.Http.DeleteAsync(new Uri($"/api/v1/servers/{server}", UriKind.Relative), Ct))
        {
            await AccessScenario.EnsureSuccessAsync(delete);
        }

        await InProcessAgent.EventuallyAsync(async () => await ServerApi.GetAsync(admin, server) is null, "server deleted");
        var allocations = await admin.Http.GetFromJsonAsync<List<AllocationView>>(
            new Uri($"/api/v1/nodes/{agent.Config.NodeId}/allocations", UriKind.Relative), AccessScenario.Json, Ct);
        Assert.All(allocations!, a => Assert.Null(a.ServerId));
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"games-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private static async Task<Guid> InstalledAsync(PanelClient admin, InProcessAgent agent, string game)
    {
        var template = await TemplateAsync(admin, game);
        var created = await CreateAsync(admin, new CreateServerRequest(agent.Config!.NodeId, template.Template.Id, game, null));
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }

    internal static async Task<TemplateDetails> TemplateAsync(PanelClient client, string key)
    {
        var templates = await client.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct);
        var template = templates!.Single(t => t.Key == key);
        return (await client.Http.GetFromJsonAsync<TemplateDetails>(new Uri($"/api/v1/templates/{template.Id}", UriKind.Relative), AccessScenario.Json, Ct))!;
    }

    internal static async Task<List<AllocationView>> AllocateAsync(PanelClient client, Guid nodeId, string address, string ports)
    {
        using var response = await client.PostAsync($"/api/v1/nodes/{nodeId}/allocations", new CreateAllocationsRequest(address, ports, null));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<AllocationView>>(AccessScenario.Json, Ct))!;
    }

    private static async Task<ServerDetails> CreateAsync(PanelClient client, CreateServerRequest request)
    {
        using var response = await client.PostAsync("/api/v1/servers", request);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
    }

    private static int? Port(ServerDetails server, string name) => server.Ports.Single(p => p.Name == name).Port;

    private static async Task<ServerSettings> SettingsAsync(PanelClient client, Guid server) =>
        (await client.Http.GetFromJsonAsync<ServerSettings>(new Uri($"/api/v1/servers/{server}/settings", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static Task<HttpResponseMessage> PutResponseAsync<T>(PanelClient client, string path, T body) =>
        client.Http.PutAsJsonAsync(new Uri(path, UriKind.Relative), body, Ct);

    private static async Task<TResult> PutAsync<TResult>(PanelClient client, string path, object body)
    {
        using var response = await PutResponseAsync(client, path, body);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<TResult>(AccessScenario.Json, Ct))!;
    }
}
