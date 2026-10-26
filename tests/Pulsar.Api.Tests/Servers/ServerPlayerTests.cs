using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Kick, ban, whitelist and admins from the panel (Pulsar 1.0.11): the game's own commands through its console, a name
/// refused rather than cleaned, the right to send console commands for admins, and the game's lists read from its files.
/// </summary>
public sealed class ServerPlayerTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AKickGoesThroughTheGamesConsoleAndIsAudited()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "players-kick");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27100-27105");

        var players = (await ServerApi.GetAsync(admin, server))!.Players!;
        Assert.Equal((false, true, 0), (players.ById, players.Spaces, players.Lists.Count));
        Assert.Equal([PlayerAction.Kick], players.Actions);

        using (var kicked = await admin.PostAsync($"/api/v1/servers/{server}/players", new { action = "kick", player = " Big Bob " }))
        {
            await AccessScenario.EnsureSuccessAsync(kicked);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Sent(agent).Contains("kick \"Big Bob\"")), "the kick reached the game");
        var audit = await AuditAsync(server);
        var entry = Assert.Single(audit, a => a.Action == AuditActions.Server.PlayerAction);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        using var metadata = JsonDocument.Parse(entry.Metadata!);
        Assert.Equal(("kick", "Big Bob"), (metadata.RootElement.GetProperty("action").GetString(), metadata.RootElement.GetProperty("player").GetString()));
        Assert.DoesNotContain(audit, a => a.Action == AuditActions.Server.ConsoleCommand);
    }

    [DatabaseFact]
    public async Task ANameOrActionTheGameDoesNotTakeIsRefusedAndNothingIsSent()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "players-refused");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27110-27115");
        var before = Sent(agent).Count;

        // Minecraft's @a is everyone; Source's ';' ends a command; quotes end the name. None is cleaned into another player.
        foreach (var player in (string[])["@a", "Bob;quit", "Bob\" ; quit", "Bob\nquit", new string('a', 65)])
        {
            await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/players", new { action = "kick", player }), HttpStatusCode.BadRequest,
                player.Length > 64 ? null : ServerErrors.PlayerInvalid.Code);
        }

        // Counter-Strike: Source's bans take an id the query does not list: the template has none.
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/players", new { action = "ban", player = "Bob" }), HttpStatusCode.BadRequest,
            ServerErrors.PlayerActionUnsupported.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/players", new { action = "mute", player = "Bob" }), HttpStatusCode.BadRequest, null);

        Assert.Equal(before, Sent(agent).Count);
        Assert.DoesNotContain(await AuditAsync(server), a => a.Action == AuditActions.Server.PlayerAction);
    }

    [DatabaseFact]
    public async Task PlayersNeedTheirPermissionAndAdminsTheConsolesToo()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "players-rights");
        var templateId = await ShipAsync(admin, players => players["adminAdd"] = "sm_addadmin \"{{player}}\"");
        var server = await RunningAsync(admin, agent, templateId, "27120-27125");

        using var viewer = await scenario.SignedInWithAsync("players-viewer", Permissions.Server.View, Permissions.Server.ConsoleWrite);
        await ProblemAsync(await viewer.PostAsync($"/api/v1/servers/{server}/players", new { action = "kick", player = "Bob" }), HttpStatusCode.Forbidden, null);
        await ProblemAsync(await viewer.GetAsync($"/api/v1/servers/{server}/players/lists"), HttpStatusCode.Forbidden, null);

        // A moderator kicks; making someone an admin, who runs the game's commands, is as much as a console command.
        using var moderator = await scenario.SignedInWithAsync("players-moderator", Permissions.Server.View, Permissions.Server.PlayersManage);
        using (var kicked = await moderator.PostAsync($"/api/v1/servers/{server}/players", new { action = "kick", player = "Bob" }))
        {
            await AccessScenario.EnsureSuccessAsync(kicked);
        }

        await ProblemAsync(await moderator.PostAsync($"/api/v1/servers/{server}/players", new { action = "adminAdd", player = "Bob" }),
            HttpStatusCode.Forbidden, ServerErrors.PlayerAdminForbidden.Code);

        using var operatorUser = await scenario.SignedInWithAsync("players-operator", Permissions.Server.View, Permissions.Server.PlayersManage,
            Permissions.Server.ConsoleWrite);
        using (var promoted = await operatorUser.PostAsync($"/api/v1/servers/{server}/players", new { action = "adminAdd", player = "Bob" }))
        {
            await AccessScenario.EnsureSuccessAsync(promoted);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Sent(agent).Contains("sm_addadmin \"Bob\"")), "the admin reached the game");
        Assert.Single(Sent(agent), c => c.StartsWith("sm_addadmin", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task TheGamesListsAreReadFromItsFiles()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "players-lists");
        var templateId = await ShipAsync(admin, players => players["lists"] = new JsonObject
        {
            ["banned"] = new JsonObject { ["file"] = "banned-players.json", ["field"] = "name" },
            ["whitelist"] = new JsonObject { ["file"] = "whitelist.json", ["field"] = "name" },
            ["admins"] = new JsonObject { ["file"] = "cstrike/ops.json", ["field"] = "name" },
        });
        var server = await RunningAsync(admin, agent, templateId, "27130-27135");
        var details = (await ServerApi.GetAsync(admin, server))!;
        Assert.Equal([PlayerListKind.Banned, PlayerListKind.Whitelist, PlayerListKind.Admins], details.Players!.Lists);

        // What the game wrote, and what its players named themselves: each name once, and only short lines of text.
        var root = Path.Combine(agent.ServersRoot, details.Server.ShortId);
        Directory.CreateDirectory(Path.Combine(root, "cstrike"));
        await File.WriteAllTextAsync(Path.Combine(root, "banned-players.json"),
            """[{"name": "Alice", "reason": "x"}, {"name": "Bob"}, {"name": "Alice"}, {"uuid": "1"}, {"name": 7}, {"name": "a\u0007b"}, "Carol"]""", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "cstrike", "ops.json"), "{ not json", Ct);

        var lists = await ListsAsync(admin, server);
        Assert.Equal(["Alice", "Bob"], List(lists, PlayerListKind.Banned).Players);
        Assert.Equal((0, (string?)null), (List(lists, PlayerListKind.Whitelist).Players.Count, List(lists, PlayerListKind.Whitelist).ErrorCode));
        Assert.Equal(ServerPlayers.ListUnreadable, List(lists, PlayerListKind.Admins).ErrorCode);

        // More than the panel shows, or a file larger than it reads.
        await File.WriteAllTextAsync(Path.Combine(root, "whitelist.json"),
            JsonSerializer.Serialize(Enumerable.Range(0, ServerPlayers.MaxListed + 5).Select(i => new { name = $"p{i}" })), Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "cstrike", "ops.json"), $$"""[{"name": "{{new string('a', (int)ServerPlayers.MaxListBytes)}}"}]""", Ct);
        lists = await ListsAsync(admin, server);
        Assert.Equal((ServerPlayers.MaxListed, true), (List(lists, PlayerListKind.Whitelist).Players.Count, List(lists, PlayerListKind.Whitelist).Truncated));
        Assert.Equal(ServerPlayers.ListUnreadable, List(lists, PlayerListKind.Admins).ErrorCode);
    }

    private static List<string> Sent(InProcessAgent agent)
    {
        lock (agent.Rcon.Commands)
        {
            return [.. agent.Rcon.Commands];
        }
    }

    private static PlayerListView List(ServerPlayerLists lists, PlayerListKind kind) => lists.Lists.Single(l => l.Kind == kind);

    private static async Task<ServerPlayerLists> ListsAsync(PanelClient client, Guid server)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/players/lists");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ServerPlayerLists>(AccessScenario.Json, Ct))!;
    }

    /// <summary>Counter-Strike: Source's template as a newer version whose player commands <paramref name="change"/> makes.</summary>
    private async Task<Guid> ShipAsync(PanelClient admin, Action<JsonObject> change)
    {
        var version = await TemplateUpdateTests.ShipNewerAsync(factory, admin, "css", manifest => change(manifest["commands"]!["players"]!.AsObject()));
        var templates = await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct);
        return templates!.Single(t => t.Key == "css" && t.Version == version).Id;
    }

    private static async Task<Guid> RunningAsync(PanelClient admin, InProcessAgent agent, Guid templateId, string ports)
    {
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", ports);
        using var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config!.NodeId, templateId, "css", null));
        await AccessScenario.EnsureSuccessAsync(created);
        var server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        return server;
    }

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            if (code is not null)
            {
                using var problem = JsonDocument.Parse(body);
                Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
            }
        }
    }
}
