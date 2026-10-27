using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Game updates (Pulsar 1.0.9) through the panel and the real agent core with a scripted SteamCMD: a check asks Steam for
/// the latest build and compares it with the app manifest, a new build is shown, and installed by itself as a sequence
/// (the players warned, the server stopped, updated and started again), as the game updates and never beyond what its
/// author may do.
/// </summary>
public sealed class GameUpdateTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ANewBuildIsFoundAndInstalledByItselfWarningThePlayers()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "auto");
        var server = await RunningCssAsync(admin, agent, "27015-27020");

        // Up to date: what SteamCMD installed is what Steam offers.
        var current = await CheckedAsync(admin, server, v => v.Available == false);
        Assert.Equal((GameUpdatePolicy.Notify, true, true, true), (current.Policy, current.Supported, current.CanWarn, current.CanWaitForEmpty));
        Assert.Equal(("1000", "1000"), (current.Installed, current.Latest));
        Assert.True(agent.Processes.SteamCmdChecks > 0);

        agent.Processes.LatestBuilds["232330"] = "1001";
        var found = await CheckedAsync(admin, server, v => v.Available == true);
        Assert.Equal(("1000", "1001"), (found.Installed, found.Latest));
        Assert.Contains(await ServersAsync(admin), s => s.Id == server && s.GameUpdateAvailable);

        var started = (await ServerApi.GetAsync(admin, server))!.Server.LastStartedAt;
        await PutAsync(admin, server, new GameUpdateSettingsRequest(GameUpdatePolicy.Install, false, 1, "Reinicio en {time} por una actualización"));
        await PassAsync();
        var done = await FinishedAsync(admin, server);

        Assert.Equal(SequenceStatus.Succeeded, done.Running!.Status);
        Assert.Contains("say Reinicio en 1 min por una actualización", agent.Rcon.Commands);
        Assert.Contains("say Reinicio en 10 s por una actualización", agent.Rcon.Commands);
        Assert.Contains("quit", agent.Rcon.Commands);
        var after = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running && s.Server.LastStartedAt > started,
            "running again");
        Assert.Contains(after.RecentOperations, o => o.Type == OperationType.ServerUpdate && o.Status == OperationStatus.Succeeded);
        // What is installed now is checked again: the update brought the new build.
        await CheckedAsync(admin, server, v => v.Available == false && v.Installed == "1001", ask: false);

        var audit = await AuditAsync(server);
        var begun = Assert.Single(audit, a => a.Action == AuditActions.Server.GameUpdateStarted);
        Assert.Equal((AuditActorType.System, GameUpdates.ActorName), (begun.ActorType, begun.ActorDisplay));
        Assert.Contains(audit, a => a.Action == AuditActions.Server.Stopped && a.ActorType == AuditActorType.System);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.UpdateRequested && a.ActorType == AuditActorType.System);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.GameUpdatesChanged && a.ActorType == AuditActorType.User);

        // A build is installed by itself once: the same one again starts nothing.
        await PassAsync();
        Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.GameUpdateStarted);
    }

    [DatabaseFact]
    public async Task AnUpdateWaitsUntilNobodyPlaysWhenAsked()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "empty");
        var players = new[] { "Alice" };
        agent.Query.Answer = _ => agent.Query.Info("Scripted", "de_dust2", players);
        var server = await RunningCssAsync(admin, agent, "27030-27035");
        await PutAsync(admin, server, new GameUpdateSettingsRequest(GameUpdatePolicy.Install, true, 0, null));
        agent.Processes.LatestBuilds["232330"] = "1001";
        await CheckedAsync(admin, server, v => v.Available == true);
        await InProcessAgent.EventuallyAsync(async () => (await GetAsync(admin, server)).PlayersOnline == 1, "the panel knows who plays");

        await PassAsync();
        var waiting = await GetAsync(admin, server);
        Assert.True(waiting.Pending);
        Assert.Null(waiting.Running);
        Assert.Equal(1, waiting.PlayersOnline);

        players = [];
        await InProcessAgent.EventuallyAsync(async () => (await GetAsync(admin, server)).PlayersOnline == 0, "nobody plays");
        await PassAsync();
        var done = await FinishedAsync(admin, server);
        Assert.Equal(SequenceStatus.Succeeded, done.Running!.Status);
        // No warning was asked for.
        Assert.DoesNotContain(agent.Rcon.Commands, c => c.StartsWith("say ", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task AFailedUpdateStillPutsTheServerBackOnlineAndCanBeCancelledBeforeItStarts()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "failed");
        var server = await RunningCssAsync(admin, agent, "27040-27045");
        agent.Processes.LatestBuilds["232330"] = "1001";
        await CheckedAsync(admin, server, v => v.Available == true);

        // Waiting for the players to leave, then skipped: this build is not installed by itself.
        agent.Query.Answer = _ => agent.Query.Info("Scripted", "de_dust2", "Bob");
        await PutAsync(admin, server, new GameUpdateSettingsRequest(GameUpdatePolicy.Install, true, 0, null));
        await InProcessAgent.EventuallyAsync(async () => (await GetAsync(admin, server)).PlayersOnline == 1, "the panel knows who plays");
        using (var cancel = await admin.PostAsync($"/api/v1/servers/{server}/game-updates/cancel"))
        {
            await AccessScenario.EnsureSuccessAsync(cancel);
        }

        Assert.False((await GetAsync(admin, server)).Pending);

        // "Update and restart" by hand, with an update SteamCMD cannot do: the server comes back anyway.
        agent.Processes.SteamCmdGate = new TaskCompletionSource();
        using (var install = await admin.PostAsync($"/api/v1/servers/{server}/game-updates/install"))
        {
            await AccessScenario.EnsureSuccessAsync(install);
        }

        await InProcessAgent.EventuallyAsync(async () =>
        {
            await DriveAsync();
            return (await ServerApi.GetAsync(admin, server))!.Server.ActiveOperation?.Type == OperationType.ServerUpdate;
        }, "updating");
        var operation = (await ServerApi.GetAsync(admin, server))!.Server.ActiveOperation!;
        using (var cancelled = await admin.PostAsync($"/api/v1/servers/{server}/operations/{operation.Id}/cancel"))
        {
            await AccessScenario.EnsureSuccessAsync(cancelled);
        }

        var done = await FinishedAsync(admin, server);
        Assert.Equal(SequenceStatus.Failed, done.Running!.Status);
        Assert.Equal("operation.cancelled", done.Running.ErrorCode);
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running again");
    }

    [DatabaseFact]
    public async Task CancellingAnUpdateThatRunsCancelsItsDownloadAndPutsTheServerBack()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "cancel");
        var server = await RunningCssAsync(admin, agent, "27070-27075");
        agent.Processes.LatestBuilds["232330"] = "1001";
        await CheckedAsync(admin, server, v => v.Available == true);
        // Asked again at once: the same answer, no second check (each one signs in to Steam).
        var checks = agent.Processes.SteamCmdChecks;
        using (var again = await admin.PostAsync($"/api/v1/servers/{server}/game-updates/check"))
        {
            await AccessScenario.EnsureSuccessAsync(again);
        }

        Assert.Equal(checks, agent.Processes.SteamCmdChecks);

        agent.Processes.SteamCmdGate = new TaskCompletionSource();
        using (var install = await admin.PostAsync($"/api/v1/servers/{server}/game-updates/install"))
        {
            await AccessScenario.EnsureSuccessAsync(install);
        }

        await InProcessAgent.EventuallyAsync(async () =>
        {
            await DriveAsync();
            return (await ServerApi.GetAsync(admin, server))!.Server.ActiveOperation?.Type == OperationType.ServerUpdate;
        }, "updating");
        using (var cancel = await admin.PostAsync($"/api/v1/servers/{server}/game-updates/cancel"))
        {
            await AccessScenario.EnsureSuccessAsync(cancel);
        }

        var done = await FinishedAsync(admin, server);
        Assert.Equal(SequenceStatus.Cancelled, done.Running!.Status);
        var after = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running again");
        Assert.Contains(after.RecentOperations, o => o.Type == OperationType.ServerUpdate && o.Status == OperationStatus.Cancelled);
        Assert.Contains(await AuditAsync(server), a => a.Action == AuditActions.Server.GameUpdateCancelled && a.ActorType == AuditActorType.User);
    }

    [DatabaseFact]
    public async Task InstallingByItselfNeedsTheRightsItUses()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "rights");
        var server = await RunningCssAsync(admin, agent, "27050-27055");

        using var updater = await scenario.SignedInWithAsync("updater", Permissions.Server.View, Permissions.Server.Install);
        await ProblemAsync(await updater.PutAsync($"/api/v1/servers/{server}/game-updates",
            new GameUpdateSettingsRequest(GameUpdatePolicy.Install, false, 0, null)), HttpStatusCode.Forbidden, ServerErrors.GameUpdatesForbidden.Code);
        await ProblemAsync(await updater.PostAsync($"/api/v1/servers/{server}/game-updates/install"), HttpStatusCode.Forbidden,
            ServerErrors.GameUpdatesForbidden.Code);
        using (var notify = await updater.PutAsync($"/api/v1/servers/{server}/game-updates", new GameUpdateSettingsRequest(GameUpdatePolicy.Off, false, 0, null)))
        {
            await AccessScenario.EnsureSuccessAsync(notify); // not installing: the right to update is enough
        }

        // Warning the players sends console commands.
        using var operatorClient = await scenario.SignedInWithAsync("operator", Permissions.Server.View, Permissions.Server.Install, Permissions.Server.Stop,
            Permissions.Server.Start);
        await ProblemAsync(await operatorClient.PutAsync($"/api/v1/servers/{server}/game-updates",
            new GameUpdateSettingsRequest(GameUpdatePolicy.Install, false, 5, null)), HttpStatusCode.Forbidden, ServerErrors.GameUpdatesForbidden.Code);
        using (var silent = await operatorClient.PutAsync($"/api/v1/servers/{server}/game-updates",
                   new GameUpdateSettingsRequest(GameUpdatePolicy.Install, false, 0, null)))
        {
            await AccessScenario.EnsureSuccessAsync(silent);
        }

        using var viewer = await scenario.SignedInWithAsync("viewer", Permissions.Server.View);
        Assert.Equal(GameUpdatePolicy.Install, (await GetAsync(viewer, server)).Policy);
        using var refused = await viewer.PostAsync($"/api/v1/servers/{server}/game-updates/check");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [DatabaseFact]
    public async Task AServerFollowsTheSteamBranchChosenAfterABackup()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "branch");
        agent.Processes.Branches["232330"] = [("previous_build", "900", "Previous build", false), ("secret", "950", null, true)];
        var server = await RunningCssAsync(admin, agent, "27070-27075");

        // The check lists the game's branches, as Steam names them.
        var listed = await CheckedAsync(admin, server, v => v.Branches is { Count: 3 });
        Assert.Equal((true, null), (listed.BranchesSupported, listed.SteamBranch));
        Assert.Equal([("public", "1000", false), ("previous_build", "900", false), ("secret", "950", true)],
            listed.Branches!.Select(b => (b.Name, b.Build, b.PasswordRequired)));
        Assert.Equal("Previous build", listed.Branches![1].Description);

        // The server is stopped first; the branch is one the check found, and without a password.
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("previous_build")),
            HttpStatusCode.Conflict, ServerErrors.MustBeStopped.Code);
        await ServerApi.CommandAsync(admin, server, "stop");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped");
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("experimental")),
            HttpStatusCode.Conflict, ServerErrors.SteamBranchUnknown.Code);
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("secret")),
            HttpStatusCode.Conflict, ServerErrors.SteamBranchPasswordRequired.Code);
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("-beta")),
            HttpStatusCode.Conflict, ServerErrors.SteamBranchUnknown.Code);

        // It backs the server up, then installs the branch (written as Steam names it), every file verified.
        using var updater = await scenario.SignedInWithAsync("branch-updater", Permissions.Server.View, Permissions.Server.Install);
        await ProblemAsync(await updater.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("previous_build")),
            HttpStatusCode.Forbidden, ServerErrors.SteamBranchForbidden.Code);
        var updates = agent.Processes.SteamBetas.Count;
        GameUpdateView changed;
        using (var put = await admin.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("Previous_Build")))
        {
            await AccessScenario.EnsureSuccessAsync(put);
            changed = (await put.Content.ReadFromJsonAsync<GameUpdateView>(AccessScenario.Json, Ct))!;
        }

        Assert.Equal(("previous_build", true), (changed.SteamBranch, changed.Running?.SteamBranch));
        var done = await FinishedAsync(admin, server);
        Assert.Equal(SequenceStatus.Succeeded, done.Running!.Status);
        var operations = (await ServerApi.GetAsync(admin, server))!.RecentOperations;
        Assert.Contains(operations, o => o.Type == OperationType.BackupCreate && o.Status == OperationStatus.Succeeded);
        Assert.Contains(operations, o => o.Type == OperationType.ServerUpdate && o.Status == OperationStatus.Succeeded);
        Assert.Equal(["previous_build"], agent.Processes.SteamBetas.Skip(updates));
        // The branch's build is installed, and checked against that branch from now on.
        await CheckedAsync(admin, server, v => v.Installed == "900" && v.Latest == "900" && v.Available == false, ask: false);
        Assert.Equal(GameUpdates.BranchActorName,
            Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.UpdateRequested && a.ActorType == AuditActorType.System).ActorDisplay);
        Assert.Contains(await AuditAsync(server), a => a.Action == AuditActions.Server.SteamBranchChanged && a.ActorType == AuditActorType.User);

        // Going back is "public" said out loud: SteamCMD stays on a branch it installed until told another.
        updates = agent.Processes.SteamBetas.Count;
        using (var back = await admin.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("public")))
        {
            await AccessScenario.EnsureSuccessAsync(back);
        }

        Assert.Equal(SequenceStatus.Succeeded, (await FinishedAsync(admin, server)).Running!.Status);
        Assert.Equal(["public"], agent.Processes.SteamBetas.Skip(updates));
        await CheckedAsync(admin, server, v => v.Installed == "1000" && v.Available == false, ask: false);
    }

    [DatabaseFact]
    public async Task AChangeOfBranchThatDoesNotEndWellPutsBackTheFormerBranch()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "branch-undo");
        agent.Processes.Branches["232330"] = [("previous_build", "900", null, false)];
        var server = await RunningCssAsync(admin, agent, "27080-27085");
        await CheckedAsync(admin, server, v => v.Branches is { Count: 2 });
        await ServerApi.CommandAsync(admin, server, "stop");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped");

        // Cancelled while it installs the branch: its backup was taken, the branch was not.
        agent.Processes.SteamCmdGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using (var put = await admin.PutAsync($"/api/v1/servers/{server}/game-updates/branch", new SteamBranchRequest("previous_build")))
            {
                await AccessScenario.EnsureSuccessAsync(put);
            }

            await InProcessAgent.EventuallyAsync(async () =>
            {
                await DriveAsync();
                return (await GetAsync(admin, server)).Running?.StepKind == SequenceStepKind.Update;
            }, "the branch being installed");
            using var cancel = await admin.PostAsync($"/api/v1/servers/{server}/game-updates/cancel");
            await AccessScenario.EnsureSuccessAsync(cancel);
        }
        finally
        {
            agent.Processes.SteamCmdGate.TrySetResult();
        }

        var view = await FinishedAsync(admin, server);
        Assert.Equal(SequenceStatus.Cancelled, view.Running!.Status);
        // Back on the branch it followed: nothing installs the new one later without its backup.
        Assert.Equal((null, "public"), (view.SteamBranch, view.CurrentBranch));
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Servers.AsNoTracking()
            .SingleAsync(s => s.Id == new ServerId(server), Ct);
        Assert.Null(stored.SteamBranch);
    }

    [DatabaseFact]
    public async Task ANodeCannotReportAChecksNobodyAskedFor()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "unasked");
        var server = await RunningCssAsync(admin, agent, "27060-27065");
        await CheckedAsync(admin, server, v => v.Available == false);

        // A node that says on its own that its server has an update, with text it made up.
        await agent.Channel!.DeliverAsync(nameof(IAgentHub.GameUpdateChecked),
            new GameUpdateCheck(server, "1000", "6666", true, null, null, DateTimeOffset.UtcNow), Ct);

        var view = await GetAsync(admin, server);
        Assert.Equal(("1000", false), (view.Latest, view.Available));
    }

    private static async Task<Guid> RunningCssAsync(PanelClient admin, InProcessAgent agent, string ports)
    {
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", ports);
        var server = await GameServerTests.InstalledAsync(admin, agent, "css");
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        return server;
    }

    private Task<GameUpdateView> CheckedAsync(PanelClient admin, Guid server, Func<GameUpdateView, bool> found, bool ask = true) =>
        SequenceDriver.CheckedAsync(factory, admin, server, found, ask);

    /// <summary>Moves the sequence on (its waits pass at once) until the automatic update ended.</summary>
    private async Task<GameUpdateView> FinishedAsync(PanelClient admin, Guid server)
    {
        GameUpdateView? view = null;
        ServerDetails? details = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await DriveAsync();
            view = await GetAsync(admin, server);
            details = await ServerApi.GetAsync(admin, server);
            return view.Running is { Status: not SequenceStatus.Running };
        }, () => $"the update ended (last: step {view?.Running?.Step} {view?.Running?.StepKind} {view?.Running?.Status} {view?.Running?.ErrorCode} "
            + $"wake {view?.Running?.WakeAt:O}; server {details?.Server.RuntimeState} {details?.Server.DesiredState} {details?.Server.LastStateReason} "
            + $"operation {details?.Server.ActiveOperation?.Type})");
        return view!;
    }

    private Task PassAsync() => SequenceDriver.GameUpdatesPassAsync(factory);

    private Task DriveAsync() => SequenceDriver.DriveAsync(factory);

    private static Task<GameUpdateView> GetAsync(PanelClient client, Guid server) => SequenceDriver.GameUpdatesAsync(client, server);

    private static async Task<List<ServerSummary>> ServersAsync(PanelClient client) =>
        (await client.Http.GetFromJsonAsync<List<ServerSummary>>(new Uri("/api/v1/servers", UriKind.Relative), AccessScenario.Json, Ct))!;

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

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"game-updates-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
