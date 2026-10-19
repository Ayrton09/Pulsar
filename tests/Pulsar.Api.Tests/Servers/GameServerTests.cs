using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Nodes;
using Pulsar.Api.Realtime;
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

    [DatabaseFact]
    public async Task LimitsReachTheRunningGameAndATooHungryOneSaysWhyItStopped()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "limits");
        await AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await InstalledAsync(admin, agent, "css");

        var limited = await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/limits", new ServerLimitsRequest(25, 1024));
        Assert.Equal(new ServerLimitsView(25, 1024, Server.DefaultDatabaseLimit), limited.Limits);
        using (var invalid = await PutResponseAsync(admin, $"/api/v1/servers/{server}/limits", new ServerLimitsRequest(0, 64)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        var game = agent.Processes.Started.Last(p => !p.IsSteamCmd);
        Assert.Equal(new Pulsar.Agent.Core.Processes.ProcessLimits(25, 1024L * 1024 * 1024), game.StartLimits);

        // Changed while it runs: the running game gets the new caps at once.
        await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/limits", new ServerLimitsRequest(null, 2048));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(game.AppliedLimits.LastOrDefault() == new Pulsar.Agent.Core.Processes.ProcessLimits(null, 2048L * 1024 * 1024)),
            "new limits applied to the running game");

        game.ReachedMemoryLimit = true;
        game.Exit(unchecked((int)0xC0000017));
        var crashed = await ServerApi.WaitForAsync(admin, server, s => s.Server.LastStateReason == ServerStateReason.MemoryLimit, "crashed at its memory limit");
        Assert.Equal(unchecked((int)0xC0000017), crashed.Server.LastExitCode);
        await ServerApi.CommandAsync(admin, server, "stop");
    }

    [DatabaseFact]
    public async Task AServerMovesToANewerVersionOfItsTemplate()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "upgrade");
        await AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await InstalledAsync(admin, agent, "css");
        var original = (await ServerApi.GetAsync(admin, server))!;
        Assert.Equal("1.0.0", original.TemplateVersion);

        // A newer version of the same game: a new setting written to its configuration file.
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(GamePath("css"), Ct))!.AsObject();
        manifest["version"] = "1.1.0";
        manifest["variables"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
            """{ "key": "contact", "label": "Contact", "type": "string", "default": "admin at example", "pattern": "^[a-z ]{0,32}$" }"""));
        manifest["configFiles"]![0]!["set"]!["sv_contact"] = "{{var.contact}}";
        Guid next;
        using (var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest.ToJsonString())))
        {
            await AccessScenario.EnsureSuccessAsync(imported);
            next = (await imported.Content.ReadFromJsonAsync<TemplateDetails>(AccessScenario.Json, Ct))!.Template.Id;
        }

        Assert.Equal(new TemplateUpgradeView(next, "1.1.0"), (await ServerApi.GetAsync(admin, server))!.TemplateUpgrade);
        var upgraded = await PutAsync<ServerDetails>(admin, $"/api/v1/servers/{server}/template", new UpgradeTemplateRequest(next));
        Assert.Equal("1.1.0", upgraded.TemplateVersion);
        Assert.Null(upgraded.TemplateUpgrade);
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "provisioned and installed again");

        // The agent runs the new version.
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        var config = await File.ReadAllTextAsync(Path.Combine(agent.ServersRoot, original.Server.ShortId, "cstrike", "cfg", "server.cfg"), Ct);
        Assert.Contains("sv_contact \"admin at example\"", config, StringComparison.Ordinal);

        // Never back, and never while running.
        using (var back = await PutResponseAsync(admin, $"/api/v1/servers/{server}/template", new UpgradeTemplateRequest(original.Server.TemplateId)))
        {
            Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        }

        await ServerApi.CommandAsync(admin, server, "stop");
    }

    internal static string GamePath(string game)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "games", game, "manifest.json");
    }

    [DatabaseFact]
    public async Task WhoIsPlayingReachesTheBrowserCleaned()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "query");
        await AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await InstalledAsync(admin, agent, "css");
        var asked = new List<Pulsar.Manifests.QueryEndpoint>();
        agent.Query.Answer = endpoint =>
        {
            lock (asked)
            {
                asked.Add(endpoint);
            }

            return agent.Query.Info("Pulsar‮ test", "de_dust2", "Ana", "<b>Bob</b>");
        };
        await using var browser = await BrowserHub.ConnectAsync(factory, admin);
        await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", server, Ct);

        await ServerApi.CommandAsync(admin, server, "start");

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(browser.Metrics.Any(m => m.ServerId == server && m.Query is not null)), "players pushed");
        var query = browser.Metrics.Last(m => m.ServerId == server && m.Query is not null).Query!;
        Assert.Equal("Pulsar test", query.Name); // the agent's text is cleaned again by the panel
        Assert.Equal(("de_dust2", 2, 16), (query.Map, query.Players, query.MaxPlayers));
        Assert.Equal(["Ana", "<b>Bob</b>"], query.PlayerList.Select(p => p.Name)); // plain text for the page to show as such
        Assert.Equal(60, query.PlayerList[0].ConnectedSeconds);
        lock (asked)
        {
            Assert.Equal(new Pulsar.Manifests.QueryEndpoint(Pulsar.Manifests.QueryProtocol.A2s, "0.0.0.0", 27015), asked[0]); // the game port, every interface (the agent finds where it answers)
        }

        Assert.NotNull((await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", server, Ct))!.Metrics!.Query); // a page opened now
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
        // The list shows where players connect and which game it is (for its artwork); a game without a "game" port
        // (the test game has no ports) shows no address.
        Assert.Equal(new ServerAddressView("0.0.0.0", 27100), first.Server.GameAddress);
        Assert.Equal(template.Template.Key, first.Server.TemplateKey);
        var testGame = await ServerApi.TestTemplateAsync(admin);
        var portless = await CreateAsync(admin, new CreateServerRequest(agent.Config.NodeId, testGame.Id, "Portless", null));
        var list = await admin.Http.GetFromJsonAsync<List<ServerSummary>>(new Uri("/api/v1/servers", UriKind.Relative), AccessScenario.Json, Ct);
        var listed = list!.Single(s => s.Id == first.Server.Id);
        Assert.Equal(new ServerAddressView("0.0.0.0", 27100), listed.GameAddress);
        Assert.Equal("css", listed.TemplateKey);
        var withoutPort = list!.Single(s => s.Id == portless.Server.Id);
        Assert.Null(withoutPort.GameAddress);
        Assert.Equal(testGame.Key, withoutPort.TemplateKey);

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

    internal static async Task<Guid> InstalledAsync(PanelClient admin, InProcessAgent agent, string game)
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
        var template = templates!.Single(t => t.Key == key && t.Source == TemplateSource.Bundled); // not a newer version a test imported
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
