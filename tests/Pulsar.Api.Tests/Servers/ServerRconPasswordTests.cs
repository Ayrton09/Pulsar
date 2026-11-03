using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// An RCON password chosen for a server (Pulsar 1.0.29), for programs other than the panel: the game starts with it and
/// the agent's own RCON uses it (the scripted RCON lets in only the password the game started with). Chosen only while
/// the server is stopped, by whoever may change its protected settings; never returned, never audited with it.
/// </summary>
public sealed class ServerRconPasswordTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const string Chosen = "Chosen_Pass.word1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AChosenPasswordIsTheGamesAndTheAgentsUntilItGoesBackToOneTheAgentMakes()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "rcon-chosen");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await GameServerTests.InstalledAsync(admin, agent, "css");
        var settings = await SettingsAsync(admin, server);
        Assert.True(settings.HasRcon);
        Assert.False(settings.RconPasswordChosen);

        // Not one Pulsar uses: too short, or what a command line reads as another command.
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest("short")), HttpStatusCode.BadRequest,
            ServerErrors.RconPasswordInvalid.Code);
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest("long-enough password;quit")),
            HttpStatusCode.BadRequest, ServerErrors.RconPasswordInvalid.Code);
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest(Chosen + "\n")),
            HttpStatusCode.BadRequest, ServerErrors.RconPasswordInvalid.Code);

        // The protected settings' permission, not the startup's.
        using (var startup = await scenario.SignedInWithAsync("rcon-startup", Permissions.Server.View, Permissions.Server.StartupEdit))
        {
            await ProblemAsync(await startup.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest(Chosen)), HttpStatusCode.Forbidden,
                null);
        }

        using (var chosen = await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest(Chosen)))
        {
            var body = await chosen.Content.ReadAsStringAsync(Ct);
            Assert.True(chosen.IsSuccessStatusCode, body);
            Assert.DoesNotContain(Chosen, body, StringComparison.Ordinal);
            Assert.True(JsonSerializer.Deserialize<ServerSettings>(body, AccessScenario.Json)!.RconPasswordChosen);
        }

        Assert.Equal(Chosen, agent.StoredSpec(server).RconPassword);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var stored = await db.Servers.AsNoTracking().SingleAsync(s => s.Id == new ServerId(server), Ct);
            Assert.DoesNotContain(Chosen, System.Text.Encoding.UTF8.GetString(stored.RconPasswordProtected!), StringComparison.Ordinal);
            var audited = await db.AuditLog.AsNoTracking().SingleAsync(a => a.ServerId == new ServerId(server) && a.Action == AuditActions.Server.RconPasswordChosen, Ct);
            Assert.DoesNotContain(Chosen, audited.Metadata ?? "", StringComparison.Ordinal);
        }

        // One per server: another of the actor's servers cannot have it too (a plugin of one would get into the other).
        var other = await GameServerTests.InstalledAsync(admin, agent, "css");
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{other}/rcon-password", new SetRconPasswordRequest(Chosen)), HttpStatusCode.Conflict,
            ServerErrors.RconPasswordReused.Code);
        using (var another = await admin.PutAsync($"/api/v1/servers/{other}/rcon-password", new SetRconPasswordRequest(Chosen + "2")))
        {
            await AccessScenario.EnsureSuccessAsync(another);
        }

        // The game starts with it, and the agent's RCON gets in with it (ready, then a stop by quit).
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "ready over RCON");
        Assert.Equal(Chosen, RconArgument(agent));

        // Never while it runs: the game keeps the password it started with.
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest(null)), HttpStatusCode.Conflict,
            ServerErrors.MustBeStopped.Code);
        await ServerApi.CommandAsync(admin, server, "stop");
        var stopped = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped over RCON");
        Assert.Equal(0, stopped.Server.LastExitCode);
        Assert.Contains("quit", agent.Rcon.Commands);

        // Back to one the agent makes: a new one, which whoever knew the chosen one does not know.
        using (var cleared = await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest(null)))
        {
            await AccessScenario.EnsureSuccessAsync(cleared);
        }

        Assert.False((await SettingsAsync(admin, server)).RconPasswordChosen);
        Assert.Null(agent.StoredSpec(server).RconPassword);
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "ready over RCON again");
        var made = RconArgument(agent);
        Assert.NotEqual(Chosen, made);
        Assert.Equal(32, made?.Length);
        await ServerApi.CommandAsync(admin, server, "stop");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped again");
    }

    [DatabaseFact]
    public async Task AGameWithoutAnRconConsoleHasNoPasswordToChoose()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "rcon-none");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "2456-2460");
        var valheim = await GameServerTests.TemplateAsync(admin, "valheim");
        Guid server;
        using (var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config.NodeId, valheim.Template.Id, "No RCON", null,
                   Variables: new() { ["password"] = "secret-join" })))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        }

        Assert.False((await SettingsAsync(admin, server)).HasRcon);

        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest(Chosen)), HttpStatusCode.BadRequest,
            ServerErrors.RconUnsupported.Code);
    }

    [DatabaseFact]
    public async Task TwoServersChoosingTheSamePasswordAtOnceDoNotBothGetIt()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "rcon-at-once");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27015-27030");
        var first = await GameServerTests.InstalledAsync(admin, agent, "css");
        var second = await GameServerTests.InstalledAsync(admin, agent, "css");

        // Its own password: the tests of this class share the panel, and the others choose theirs.
        const string atOnce = "At_Once.Pass1";
        var answers = await Task.WhenAll(
            admin.PutAsync($"/api/v1/servers/{first}/rcon-password", new SetRconPasswordRequest(atOnce)),
            admin.PutAsync($"/api/v1/servers/{second}/rcon-password", new SetRconPasswordRequest(atOnce)));
        try
        {
            Assert.Single(answers, a => a.IsSuccessStatusCode);
            await ProblemAsync(Assert.Single(answers, a => !a.IsSuccessStatusCode), HttpStatusCode.Conflict, ServerErrors.RconPasswordReused.Code);
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    private static string? RconArgument(InProcessAgent agent) =>
        agent.Processes.Started.Last(p => !p.IsSteamCmd).Arguments.SkipWhile(a => a != "+rcon_password").Skip(1).FirstOrDefault();

    private static async Task<ServerSettings> SettingsAsync(PanelClient client, Guid server) =>
        (await client.Http.GetFromJsonAsync<ServerSettings>(new Uri($"/api/v1/servers/{server}/settings", UriKind.Relative), AccessScenario.Json, Ct))!;

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, name);
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    internal static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
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

/// <summary>A server whose RCON password was chosen, and a node whose agent cannot take one (older than 1.0.29).</summary>
public sealed class ServerRconPasswordOlderAgentTests(OlderAgentApiFactory factory) : IClassFixture<OlderAgentApiFactory>
{
    [DatabaseFact]
    public async Task ItIsNotMovedToAnAgentThatCannotTakeItAndGoesBackToTheAgentsOwnThere()
    {
        factory.HiddenCapability = null;
        using var admin = await factory.CreateAdminClientAsync();
        await using var first = await ConnectedAgentAsync(admin, "rcon-moves-from");
        await using var second = await ConnectedAgentAsync(admin, "rcon-moves-to");
        await GameServerTests.AllocateAsync(admin, first.Config!.NodeId, "0.0.0.0", "27015-27020");
        await GameServerTests.AllocateAsync(admin, second.Config!.NodeId, "0.0.0.0", "27015-27020");
        var server = await GameServerTests.InstalledAsync(admin, first, "css");
        using (var chosen = await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest("Chosen_Pass.word1")))
        {
            await AccessScenario.EnsureSuccessAsync(chosen);
        }

        factory.HiddenCapability = AgentCapabilities.ChosenRconPassword;
        try
        {
            // Moved there, it would never start again.
            await ServerRconPasswordTests.ProblemAsync(
                await admin.PostAsync($"/api/v1/servers/{server}/transfer", new TransferServerRequest(second.Config.NodeId)), HttpStatusCode.Conflict,
                ServerErrors.RconPasswordUnsupported.Code);

            // A chosen one cannot be set there; going back to the agent's own needs nothing of it.
            await ServerRconPasswordTests.ProblemAsync(
                await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest("Another_Pass.word2")), HttpStatusCode.Conflict,
                ServerErrors.RconPasswordUnsupported.Code);
            using (var cleared = await admin.PutAsync($"/api/v1/servers/{server}/rcon-password", new SetRconPasswordRequest(null)))
            {
                await AccessScenario.EnsureSuccessAsync(cleared);
                Assert.False(JsonSerializer.Deserialize<ServerSettings>(await cleared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                    AccessScenario.Json)!.RconPasswordChosen);
            }
        }
        finally
        {
            factory.HiddenCapability = null;
        }
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
