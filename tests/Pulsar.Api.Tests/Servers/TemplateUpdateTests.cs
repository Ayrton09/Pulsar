using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulsar.Api.Agents;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Template updates (Pulsar 1.0.10) through the panel and the real agent core: a newer version of a server's game
/// template is shown and notified, and, when the server's updates say so and a Pulsar update brought it (put in the
/// database here as the panel puts the bundled ones), moved to by itself as a game update is (the players warned, the
/// server stopped, provisioned and installed again with it, started again), once its checks pass, and never beyond what
/// its author may do.
/// </summary>
public sealed class TemplateUpdateTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ANewerTemplateIsNotifiedThenMovedToByItselfWarningThePlayers()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "auto");
        var server = await RunningCssAsync(admin, agent, "27015-27020");
        var bundled = (await ServerApi.GetAsync(admin, server))!.TemplateVersion;

        var view = await GetAsync(admin, server);
        Assert.Equal((GameUpdatePolicy.Notify, false), (view.TemplatePolicy, view.TemplatePending));

        // Notified (once), not moved to: the server's updates only tell by default.
        var newer = await ShipNewerAsync(factory, admin, "css");
        await PassesUntilAsync(async () => (await WatchAsync(server)).NotifiedTemplate == newer, "notified");
        view = await GetAsync(admin, server);
        Assert.Equal((newer, newer, false), (view.NewerTemplate, view.AutoTemplate, view.TemplatePending));
        Assert.Equal(bundled, (await ServerApi.GetAsync(admin, server))!.TemplateVersion);

        var started = (await ServerApi.GetAsync(admin, server))!.Server.LastStartedAt;
        await PutAsync(admin, server,
            new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 1, "Reinicio en {time} por una plantilla nueva", GameUpdatePolicy.Install));
        await PassAsync();
        var done = await FinishedAsync(admin, server);

        Assert.Equal((SequenceStatus.Succeeded, true), (done.Running!.Status, done.Running.Template));
        Assert.Contains("say Reinicio en 1 min por una plantilla nueva", agent.Rcon.Commands);
        Assert.Contains("say Reinicio en 10 s por una plantilla nueva", agent.Rcon.Commands);
        Assert.Contains("quit", agent.Rcon.Commands);
        var after = await ServerApi.WaitForAsync(admin, server,
            s => s.Server.RuntimeState == ServerRuntimeState.Running && s.Server.LastStartedAt > started, "running again");
        Assert.Equal((newer, (TemplateUpgradeView?)null), (after.TemplateVersion, after.TemplateUpgrade));
        Assert.Contains(after.RecentOperations, o => o.Type == OperationType.ServerProvision && o.Status == OperationStatus.Succeeded);
        Assert.Contains(after.RecentOperations, o => o.Type == OperationType.ServerInstall && o.Status == OperationStatus.Succeeded);
        Assert.Null((await GetAsync(admin, server)).NewerTemplate);

        var audit = await AuditAsync(server);
        var begun = Assert.Single(audit, a => a.Action == AuditActions.Server.TemplateUpdateStarted);
        Assert.Equal((AuditActorType.System, GameUpdates.ActorName), (begun.ActorType, begun.ActorDisplay));
        Assert.Contains(newer, begun.Metadata, StringComparison.Ordinal);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.TemplateChanged && a.ActorType == AuditActorType.System);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.GameUpdatesChanged && a.ActorType == AuditActorType.User
            && a.Metadata.Contains("Install", StringComparison.Ordinal));

        // A version is moved to once: the next pass starts nothing.
        await PassAsync();
        Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.TemplateUpdateStarted);
    }

    [DatabaseFact]
    public async Task AMoveWaitsUntilNobodyPlaysAndCanBeSkipped()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "empty");
        agent.Query.Answer = _ => agent.Query.Info("Scripted", "de_dust2", ["Alice"]);
        var server = await RunningCssAsync(admin, agent, "27030-27035");
        // Shipped before the server follows templates by itself: this class's tests share the panel, each one moves to its own.
        var newer = await ShipNewerAsync(factory, admin, "css");
        await PutAsync(admin, server, new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, true, 0, null, GameUpdatePolicy.Install));
        await InProcessAgent.EventuallyAsync(async () => (await GetAsync(admin, server)).PlayersOnline == 1, "the panel knows who plays");

        await PassAsync();
        var waiting = await GetAsync(admin, server);
        Assert.Equal((newer, true, (GameUpdateRunView?)null), (waiting.NewerTemplate, waiting.TemplatePending, waiting.Running));

        // Skipped before it began: this version is not moved to by itself, the server stays as it is.
        using (var cancelled = await admin.PostAsync($"/api/v1/servers/{server}/game-updates/cancel"))
        {
            await AccessScenario.EnsureSuccessAsync(cancelled);
        }

        Assert.False((await GetAsync(admin, server)).TemplatePending);
        agent.Query.Answer = _ => agent.Query.Info("Scripted", "de_dust2", []);
        await InProcessAgent.EventuallyAsync(async () => (await GetAsync(admin, server)).PlayersOnline == 0, "nobody plays");
        await PassAsync();
        var audit = await AuditAsync(server);
        Assert.DoesNotContain(audit, a => a.Action == AuditActions.Server.TemplateUpdateStarted);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.GameUpdateCancelled && a.Metadata.Contains(newer, StringComparison.Ordinal));
        var details = (await ServerApi.GetAsync(admin, server))!;
        Assert.Equal(ServerRuntimeState.Running, details.Server.RuntimeState);
        Assert.Equal(newer, details.TemplateUpgrade!.Version); // still offered by hand
    }

    [DatabaseFact]
    public async Task AMoveItsChecksRefuseWarnsAndStopsNobody()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "refused");
        var server = await RunningCssAsync(admin, agent, "27040-27045");
        var started = (await ServerApi.GetAsync(admin, server))!.Server.LastStartedAt;

        // A version whose new variable the server has no value for: it cannot be moved to as it is.
        var newer = await ShipNewerAsync(factory, admin, "css", manifest => manifest["variables"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "newRequired",
            ["label"] = "Something new",
            ["type"] = "string",
            ["required"] = true,
            ["pattern"] = "^[a-z]{1,8}$",
        }));
        await PutAsync(admin, server, new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 1, null, GameUpdatePolicy.Install));
        await PassesUntilAsync(async () => (await WatchAsync(server)).AttemptedTemplate == newer, "the move refused"); // not tried by itself again

        var view = await GetAsync(admin, server);
        Assert.Equal((newer, false, (GameUpdateRunView?)null), (view.AutoTemplate, view.TemplatePending, view.Running));
        var details = (await ServerApi.GetAsync(admin, server))!;
        Assert.Equal((ServerRuntimeState.Running, started), (details.Server.RuntimeState, details.Server.LastStartedAt));
        Assert.DoesNotContain(agent.Rcon.Commands, c => c.StartsWith("say ", StringComparison.Ordinal) || c == "quit");
        Assert.DoesNotContain(await AuditAsync(server), a => a.Action == AuditActions.Server.TemplateUpdateStarted);
        Assert.Equal(newer, details.TemplateUpgrade!.Version); // offered by hand, which says why
    }

    [DatabaseFact]
    public async Task APreReleaseAPulsarUpdateBringsIsShownButNotMovedToByItself()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "prerelease");
        var server = await RunningCssAsync(admin, agent, "27070-27075");
        var release = await ShipNewerAsync(factory, admin, "css");
        var candidate = GameServerTests.NextVersion(release) + "-rc.1";
        await ShipNewerAsync(factory, admin, "css", manifest => manifest["version"] = candidate);

        var view = await GetAsync(admin, server);
        Assert.Equal((candidate, release), (view.NewerTemplate, view.AutoTemplate)); // the release before it, if any
    }

    [DatabaseFact]
    public async Task MovingByItselfNeedsTheRightsItUsesAndAnyGameCanBeFollowed()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "rights");
        var server = await RunningCssAsync(admin, agent, "27050-27055");

        using var updater = await scenario.SignedInWithAsync("updater", Permissions.Server.View, Permissions.Server.Install);
        await ProblemAsync(await updater.PutAsync($"/api/v1/servers/{server}/game-updates",
                new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 0, null, GameUpdatePolicy.Install)), HttpStatusCode.Forbidden,
            ServerErrors.GameUpdatesForbidden.Code);
        using (var off = await updater.PutAsync($"/api/v1/servers/{server}/game-updates",
                   new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 0, null, GameUpdatePolicy.Off)))
        {
            await AccessScenario.EnsureSuccessAsync(off); // not moving: the right to update is enough
        }

        Assert.Equal(GameUpdatePolicy.Off, (await GetAsync(admin, server)).TemplatePolicy);

        // Without it, what is set stays (clients from before 1.0.10).
        using (var older = await admin.PutAsync($"/api/v1/servers/{server}/game-updates", new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 0, null)))
        {
            await AccessScenario.EnsureSuccessAsync(older);
        }

        Assert.Equal(GameUpdatePolicy.Off, (await GetAsync(admin, server)).TemplatePolicy);

        // A game whose updates cannot be checked follows its templates all the same.
        var plain = (await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "Plain")).Server.Id;
        using (var follows = await admin.PutAsync($"/api/v1/servers/{plain}/game-updates",
                   new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 0, null, GameUpdatePolicy.Install)))
        {
            await AccessScenario.EnsureSuccessAsync(follows);
        }

        Assert.Equal(GameUpdatePolicy.Install, (await GetAsync(admin, plain)).TemplatePolicy);
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{plain}/game-updates",
                new GameUpdateSettingsRequest(GameUpdatePolicy.Install, false, 0, null, GameUpdatePolicy.Install)), HttpStatusCode.BadRequest,
            ServerErrors.GameUpdatesUnsupported.Code);
    }

    /// <summary>A running server of the game's template as the repository has it (not a newer version a test added).</summary>
    internal static async Task<Guid> RunningCssAsync(PanelClient admin, InProcessAgent agent, string ports)
    {
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", ports);
        var bundled = (string)JsonNode.Parse(await File.ReadAllTextAsync(GameServerTests.GamePath("css"), Ct))!["version"]!;
        var templates = await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct);
        var template = templates!.Single(t => t.Key == "css" && t.Version == bundled);
        using (var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(agent.Config!.NodeId, template.Id, "css", null)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            var server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
            await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
                "installed");
            await ServerApi.CommandAsync(admin, server, "start");
            await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
            return server;
        }
    }

    /// <summary>
    /// The game's template as a version newer than any the panel has, as a Pulsar update brings one (bundled), changed by
    /// <paramref name="change"/> if given: this class's tests share the panel, each one's version is the newest.
    /// </summary>
    internal static async Task<string> ShipNewerAsync(PulsarApiFactory factory, PanelClient admin, string game, Action<JsonObject>? change = null)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(GameServerTests.GamePath(game), Ct))!.AsObject();
        var newer = GameServerTests.NextVersion(await NewestAsync(admin, game));
        manifest["version"] = newer;
        change?.Invoke(manifest);
        var content = manifest.ToJsonString();
        var parsed = ManifestParser.Parse(System.Text.Encoding.UTF8.GetBytes(content));
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors.Select(e => e.ToString())));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var m = parsed.Manifest!;
        db.GameTemplates.Add(GameTemplate.Create(m.Key, m.Version, m.SchemaVersion, m.Name, m.Description, TemplateSource.Bundled, content, parsed.Sha256,
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        return newer;
    }

    /// <summary>Imports the game's template as a version newer than any the panel has, as an administrator does.</summary>
    internal static async Task<string> ImportNewerAsync(PanelClient admin, string game)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(GameServerTests.GamePath(game), Ct))!.AsObject();
        var newer = GameServerTests.NextVersion(await NewestAsync(admin, game));
        manifest["version"] = newer;
        using var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest.ToJsonString()));
        await AccessScenario.EnsureSuccessAsync(imported);
        return newer;
    }

    private static async Task<string> NewestAsync(PanelClient admin, string game)
    {
        var templates = await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct);
        return templates!.Where(t => t.Key == game).Select(t => t.Version).OrderDescending(Comparer<string>.Create(GameTemplate.CompareVersions)).First();
    }

    /// <summary>Moves the sequence on (its waits pass at once) until the automatic move ended.</summary>
    private async Task<GameUpdateView> FinishedAsync(PanelClient admin, Guid server)
    {
        GameUpdateView? view = null;
        ServerDetails? details = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await SequenceDriver.DriveAsync(factory);
            view = await GetAsync(admin, server);
            details = await ServerApi.GetAsync(admin, server);
            return view.Running is { Status: not SequenceStatus.Running };
        }, () => $"the move ended (last: step {view?.Running?.Step} {view?.Running?.StepKind} {view?.Running?.Status} {view?.Running?.ErrorCode} "
            + $"{view?.Running?.Message}; server {details?.Server.RuntimeState} {details?.Server.InstallState} operation {details?.Server.ActiveOperation?.Type})");
        return view!;
    }

    private Task PassAsync() => SequenceDriver.GameUpdatesPassAsync(factory);

    /// <summary>Passes until <paramref name="done"/>: the panel's own loop makes them too, and of two at once on a server one gives way to the next.</summary>
    private Task PassesUntilAsync(Func<Task<bool>> done, string what) =>
        InProcessAgent.EventuallyAsync(async () =>
        {
            await PassAsync();
            return await done();
        }, what);

    private static Task<GameUpdateView> GetAsync(PanelClient client, Guid server) => SequenceDriver.GameUpdatesAsync(client, server);

    private static async Task PutAsync(PanelClient client, Guid server, GameUpdateSettingsRequest request)
    {
        using var response = await client.PutAsync($"/api/v1/servers/{server}/game-updates", request);
        await AccessScenario.EnsureSuccessAsync(response);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        }
    }

    private async Task<GameUpdateWatch> WatchAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().GameUpdateWatches.AsNoTracking()
            .SingleAsync(w => w.ServerId == new ServerId(server), Ct);
    }

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }

    private Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name) => ConnectedAgentAsync(factory, admin, name);

    internal static async Task<InProcessAgent> ConnectedAgentAsync(PulsarApiFactory factory, PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"template-updates-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}

/// <summary>
/// A version an administrator imports is theirs to review (Pulsar 1.0.10): shown and notified, moved to by hand, never by
/// itself, whatever the server's updates say. A panel of its own: no version a Pulsar update brought is newer here.
/// </summary>
public sealed class ImportedTemplateUpdateTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task AnImportedVersionIsShownButNeverMovedToByItself()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "imported");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27060-27065");
        using (var set = await admin.PutAsync($"/api/v1/servers/{server}/game-updates",
                   new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 0, null, GameUpdatePolicy.Install)))
        {
            await AccessScenario.EnsureSuccessAsync(set);
        }

        var imported = await TemplateUpdateTests.ImportNewerAsync(admin, "css");
        GameUpdateWatch? watch = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await SequenceDriver.GameUpdatesPassAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            watch = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().GameUpdateWatches.AsNoTracking()
                .SingleAsync(w => w.ServerId == new ServerId(server), TestContext.Current.CancellationToken);
            return watch.NotifiedTemplate == imported;
        }, "notified");
        await SequenceDriver.GameUpdatesPassAsync(factory);

        var view = await SequenceDriver.GameUpdatesAsync(admin, server);
        Assert.Equal((imported, (string?)null, false, (GameUpdateRunView?)null), (view.NewerTemplate, view.AutoTemplate, view.TemplatePending, view.Running));
        var details = (await ServerApi.GetAsync(admin, server))!;
        Assert.Equal((ServerRuntimeState.Running, imported), (details.Server.RuntimeState, details.TemplateUpgrade!.Version));
        Assert.Null(watch!.AttemptedTemplate); // told, never tried
    }
}

/// <summary>
/// A node whose agent does not read a newer version yet (its Pulsar update comes after the panel's): the move waits, and
/// happens once the agent does, rather than being refused for good (Pulsar 1.0.11).
/// </summary>
public sealed class OlderAgentTemplateUpdateTests(OlderAgentApiFactory factory) : IClassFixture<OlderAgentApiFactory>
{
    [DatabaseFact]
    public async Task AMoveWaitsForAnAgentThatReadsTheNewerVersion()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "older");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27080-27085");
        using (var set = await admin.PutAsync($"/api/v1/servers/{server}/game-updates",
                   new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 0, null, GameUpdatePolicy.Install)))
        {
            await AccessScenario.EnsureSuccessAsync(set);
        }

        // As an agent of 1.0.10 would: it does not say it reads commands for one player, which the newer version has.
        factory.HiddenCapability = AgentCapabilities.PlayerCommands;
        var newer = await TemplateUpdateTests.ShipNewerAsync(factory, admin, "css");
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await SequenceDriver.GameUpdatesPassAsync(factory);
            return (await WatchAsync(server)).NotifiedTemplate == newer;
        }, "notified");
        await SequenceDriver.GameUpdatesPassAsync(factory);

        Assert.Null((await WatchAsync(server)).AttemptedTemplate); // not refused: tried again next pass
        Assert.Equal(newer, (await SequenceDriver.GameUpdatesAsync(admin, server)).AutoTemplate);
        Assert.NotEqual(newer, (await ServerApi.GetAsync(admin, server))!.TemplateVersion);

        factory.HiddenCapability = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await SequenceDriver.DriveAsync(factory);
            return (await ServerApi.GetAsync(admin, server))!.TemplateVersion == newer;
        }, "moved once the agent reads it");
    }

    private async Task<GameUpdateWatch> WatchAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().GameUpdateWatches.AsNoTracking()
            .SingleAsync(w => w.ServerId == new ServerId(server), TestContext.Current.CancellationToken);
    }
}

/// <summary>The panel, seeing its agents without <see cref="HiddenCapability"/> while it is set.</summary>
public sealed class OlderAgentApiFactory : PulsarApiFactory
{
    public string? HiddenCapability { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentConnections>();
            services.AddSingleton<IAgentConnections>(sp => new Hiding(sp.GetRequiredService<AgentConnectionRegistry>(), this));
        });
    }

    private sealed class Hiding(IAgentConnections inner, OlderAgentApiFactory factory) : IAgentConnections
    {
        public AgentConnectionInfo? Find(NodeId node) =>
            inner.Find(node) is { } live && factory.HiddenCapability is { } hidden
                ? live with { Capabilities = [.. live.Capabilities.Where(c => c != hidden)] }
                : inner.Find(node);

        public void Disconnect(NodeId node) => inner.Disconnect(node);

        public void DisconnectEnded(NodeId node) => inner.DisconnectEnded(node);

        public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
            inner.SendAsync(node, command, actor, cancellationToken, operationId);

        public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);
    }
}
