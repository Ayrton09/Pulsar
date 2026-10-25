using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Scheduling;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Scheduled tasks of several steps and conditions (Pulsar 1.0.9) through the panel and the real agent core: the players
/// are told before a restart, a run is skipped while someone plays or when no game update is out, a run in progress can
/// be cancelled without leaving the server stopped, and each step needs the right to do it.
/// </summary>
public sealed class ScheduledStepsTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ARestartWithWarningsTellsThePlayersFirstAndRunsAsTheSchedule()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "warnings");
        var server = await RunningCssAsync(admin, agent, "27100-27105");
        var started = (await ServerApi.GetAsync(admin, server))!.Server.LastStartedAt;
        Assert.Equal(new ScheduleAbilities(CanBroadcast: true, KnowsPlayers: true, UpdatesChecked: true), (await ListAsync(admin, server)).Abilities);

        var task = await CreateAsync(admin, server, Steps("Restart with warnings", new ScheduledConditions(OnlyIfRunning: true),
            new(SequenceStepKind.Broadcast, "Restarting in 5 min"),
            new(SequenceStepKind.Wait, Seconds: 240),
            new(SequenceStepKind.Broadcast, "Restarting in 1 min"),
            new(SequenceStepKind.Wait, Seconds: 50),
            new(SequenceStepKind.Broadcast, "Restarting in 10 s"),
            new(SequenceStepKind.Wait, Seconds: 10),
            new(SequenceStepKind.Restart)));
        Assert.Equal((ScheduledAction.Sequence, 7, true), (task.Action, task.Steps!.Count, task.OnlyIfRunning));
        Assert.Equal(new SequenceStep(SequenceStepKind.Wait, Seconds: 240), task.Steps[1]);

        var run = await RunNowAsync(admin, server, task.Id);
        Assert.Equal(ScheduledRunStatus.Running, run.Status);
        Assert.NotNull(run.SequenceId);
        Assert.Equal(7, run.Steps);

        var ended = await EndedAsync(admin, server, task.Id, run.Id);
        Assert.Equal(ScheduledRunStatus.Succeeded, ended.Status);
        Assert.Equal<string>(["say Restarting in 5 min", "say Restarting in 1 min", "say Restarting in 10 s"],
            agent.Rcon.Commands.Where(c => c.StartsWith("say ", StringComparison.Ordinal)));
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running && s.Server.LastStartedAt > started,
            "restarted");

        // Each step is audited as the schedule, under the task's name.
        var audit = await AuditAsync(server);
        var restart = Assert.Single(audit, a => a.Action == AuditActions.Server.Restarted);
        Assert.Equal((AuditActorType.Schedule, "Restart with warnings"), (restart.ActorType, restart.ActorDisplay));
        Assert.Null(restart.ActorUserId);
        Assert.Equal(3, audit.Count(a => a.Action == AuditActions.Server.ConsoleCommand && a.ActorType == AuditActorType.Schedule));
        var created = Assert.Single(audit, a => a.Action == AuditActions.Server.ScheduleCreated);
        Assert.Contains("\"Broadcast\"", created.Metadata, StringComparison.Ordinal);
        Assert.Equal(ScheduledRunStatus.Succeeded, Assert.Single((await ListAsync(admin, server)).Tasks).LastRun!.Status);
    }

    [DatabaseFact]
    public async Task ConditionsSkipARunAndSayWhy()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "conditions");
        var players = new[] { "Alice" };
        agent.Query.Answer = _ => agent.Query.Info("Scripted", "de_dust2", players);
        var server = await RunningCssAsync(admin, agent, "27110-27115");

        // Nobody may be playing.
        var restart = await CreateAsync(admin, server, new ScheduledTaskRequest("Restart when empty", "0 4 * * *", "UTC", ScheduledAction.Restart, null,
            false, false, true, OnlyIfEmpty: true));
        Assert.True(restart.OnlyIfEmpty);
        await InProcessAgent.EventuallyAsync(async () => (await SequenceDriver.GameUpdatesAsync(admin, server)).PlayersOnline == 1,
            "the panel knows who plays");
        var busy = await RunNowAsync(admin, server, restart.Id);
        Assert.Equal((ScheduledRunStatus.Skipped, ScheduleErrors.NotEmpty.Code), (busy.Status, busy.ErrorCode));

        players = [];
        await InProcessAgent.EventuallyAsync(async () => (await SequenceDriver.GameUpdatesAsync(admin, server)).PlayersOnline == 0, "nobody plays");
        var started = (await ServerApi.GetAsync(admin, server))!.Server.LastStartedAt;
        Assert.Equal(ScheduledRunStatus.Succeeded, (await RunNowAsync(admin, server, restart.Id)).Status);
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running && s.Server.LastStartedAt > started,
            "restarted");

        // A game update must be out.
        var update = await CreateAsync(admin, server, Steps("Update if available", new ScheduledConditions(OnlyIfUpdateAvailable: true),
            new(SequenceStepKind.Stop),
            new(SequenceStepKind.Update),
            new(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true)));
        await SequenceDriver.CheckedAsync(factory, admin, server, v => v.Available == false);
        var none = await RunNowAsync(admin, server, update.Id);
        Assert.Equal((ScheduledRunStatus.Skipped, ScheduleErrors.NoUpdate.Code), (none.Status, none.ErrorCode));

        agent.Processes.LatestBuilds["232330"] = "1001";
        await SequenceDriver.CheckedAsync(factory, admin, server, v => v.Available == true);
        var run = await RunNowAsync(admin, server, update.Id);
        Assert.Equal(ScheduledRunStatus.Running, run.Status);
        Assert.Equal(ScheduledRunStatus.Succeeded, (await EndedAsync(admin, server, update.Id, run.Id)).Status);
        var after = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running again");
        Assert.Contains(after.RecentOperations, o => o.Type == OperationType.ServerUpdate && o.Status == OperationStatus.Succeeded);
        // What was installed is unknown until checked again (an automatic update would otherwise follow), then checked.
        Assert.NotEqual(true, (await SequenceDriver.GameUpdatesAsync(admin, server)).Available);
        await SequenceDriver.CheckedAsync(factory, admin, server, v => v.Available == false && v.Installed == "1001", ask: false);

        // With the checks off, nothing can tell: the condition cannot be set, and a task that has it is skipped.
        using (var off = await admin.PutAsync($"/api/v1/servers/{server}/game-updates", new GameUpdateSettingsRequest(GameUpdatePolicy.Off, false, 0, null)))
        {
            await AccessScenario.EnsureSuccessAsync(off);
        }

        Assert.False((await ListAsync(admin, server)).Abilities.UpdatesChecked);
        var skipped = await RunNowAsync(admin, server, update.Id);
        Assert.Equal((ScheduledRunStatus.Skipped, ScheduleErrors.UpdateChecksOff.Code), (skipped.Status, skipped.ErrorCode));
        await ProblemAsync(await admin.PutAsync($"/api/v1/servers/{server}/schedules/{update.Id}",
                Steps("Update if available", new ScheduledConditions(OnlyIfUpdateAvailable: true), new SequenceStep(SequenceStepKind.Update))),
            HttpStatusCode.BadRequest, ScheduleErrors.UpdateChecksUnavailable.Code);
    }

    [DatabaseFact]
    public async Task CancellingARunOfStepsSkipsWhatIsLeftButPutsTheServerBack()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "cancel");
        var server = await RunningCssAsync(admin, agent, "27120-27125");
        var task = await CreateAsync(admin, server, Steps("Maintenance", ScheduledConditions.None,
            new(SequenceStepKind.Broadcast, "Maintenance in 10 min"),
            new(SequenceStepKind.Wait, Seconds: 600),
            new(SequenceStepKind.Stop),
            new(SequenceStepKind.Start, OnlyIfWasRunning: true, Always: true)));

        var run = await RunNowAsync(admin, server, task.Id);
        await WaitingAsync(admin, server, task.Id, run.Id);
        // One at a time on a server: a second run is skipped while the first one goes on.
        var second = await RunNowAsync(admin, server, task.Id);
        Assert.Equal((ScheduledRunStatus.Skipped, ScheduleErrors.SequenceRunning.Code), (second.Status, second.ErrorCode));

        using (var cancel = await admin.PostAsync($"/api/v1/servers/{server}/schedules/{task.Id}/cancel"))
        {
            await AccessScenario.EnsureSuccessAsync(cancel);
        }

        var ended = await EndedAsync(admin, server, task.Id, run.Id);
        Assert.Equal((ScheduledRunStatus.Skipped, ScheduleErrors.Cancelled.Code), (ended.Status, ended.ErrorCode));
        Assert.Equal(ServerRuntimeState.Running, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);
        var audit = await AuditAsync(server);
        Assert.DoesNotContain(audit, a => a.Action == AuditActions.Server.Stopped);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.ScheduleRunCancelled && a.ActorType == AuditActorType.User);

        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules/{task.Id}/cancel"), HttpStatusCode.Conflict,
            ScheduleErrors.SequenceOver.Code);
    }

    [DatabaseFact]
    public async Task ARunWhoseStepsEndedUnheardOfTakesTheirOutcomeAtTheNextPass()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "unheard");
        var server = await RunningCssAsync(admin, agent, "27130-27135");
        var task = await CreateAsync(admin, server, Steps("Long wait", ScheduledConditions.None, new SequenceStep(SequenceStepKind.Wait, Seconds: 3600)));
        var run = await RunNowAsync(admin, server, task.Id);
        await WaitingAsync(admin, server, task.Id, run.Id);

        // The steps fail and the panel stops before the run hears of it.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var sequence = await db.ServerSequences.SingleAsync(s => s.Id == new ServerSequenceId(run.SequenceId!.Value), Ct);
            sequence.StepFailed("server.busy", "The node was busy.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(ScheduledRunStatus.Running, (await RunAsync(admin, server, task.Id, run.Id)).Status);
        await factory.Services.GetRequiredService<Scheduler>().RunDueAsync(DateTimeOffset.UtcNow, Ct);
        var ended = await RunAsync(admin, server, task.Id, run.Id);
        Assert.Equal((ScheduledRunStatus.Failed, "server.busy"), (ended.Status, ended.ErrorCode));
    }

    [DatabaseFact]
    public async Task EachStepNeedsTheRightToDoItAndTheGameToAllowIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "rights");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27140-27145");
        var server = await GameServerTests.InstalledAsync(admin, agent, "css");
        var warned = Steps("Restart with warnings", ScheduledConditions.None, new(SequenceStepKind.Broadcast, "Restarting"),
            new(SequenceStepKind.Restart));

        using var restarter = await scenario.SignedInWithAsync("restarter", Permissions.Server.View, Permissions.Server.SchedulesView,
            Permissions.Server.SchedulesEdit, Permissions.Server.Restart);
        await ProblemAsync(await restarter.PostAsync($"/api/v1/servers/{server}/schedules", warned), HttpStatusCode.Forbidden, ScheduleErrors.Forbidden.Code);
        await CreateAsync(restarter, server, Steps("Quiet restart", ScheduledConditions.None, new(SequenceStepKind.Wait, Seconds: 60),
            new(SequenceStepKind.Restart)));

        using var operatorClient = await scenario.SignedInWithAsync("operator", Permissions.Server.View, Permissions.Server.SchedulesView,
            Permissions.Server.SchedulesEdit, Permissions.Server.Restart, Permissions.Server.ConsoleWrite);
        var task = await CreateAsync(operatorClient, server, warned);
        // Whoever may not message the players may not change, run or cancel what does.
        await ProblemAsync(await restarter.PutAsync($"/api/v1/servers/{server}/schedules/{task.Id}", warned with { Name = "Renamed" }),
            HttpStatusCode.Forbidden, ScheduleErrors.Forbidden.Code);
        await ProblemAsync(await restarter.PostAsync($"/api/v1/servers/{server}/schedules/{task.Id}/run"), HttpStatusCode.Forbidden,
            ScheduleErrors.Forbidden.Code);
        await ProblemAsync(await restarter.PostAsync($"/api/v1/servers/{server}/schedules/{task.Id}/cancel"), HttpStatusCode.Forbidden,
            ScheduleErrors.Forbidden.Code);

        // What runs is what the task did when it began: changing the task since does not let the restarter cancel it.
        var waits = await CreateAsync(operatorClient, server, Steps("Warn later", ScheduledConditions.None, new(SequenceStepKind.Broadcast, "Restarting"),
            new(SequenceStepKind.Wait, Seconds: 600), new(SequenceStepKind.Restart)));
        var running = await RunNowAsync(operatorClient, server, waits.Id);
        await WaitingAsync(admin, server, waits.Id, running.Id);
        using (var changed = await admin.PutAsync($"/api/v1/servers/{server}/schedules/{waits.Id}",
                   Steps("Warn later", ScheduledConditions.None, new SequenceStep(SequenceStepKind.Wait, Seconds: 60))))
        {
            await AccessScenario.EnsureSuccessAsync(changed);
        }

        await ProblemAsync(await restarter.PostAsync($"/api/v1/servers/{server}/schedules/{waits.Id}/cancel"), HttpStatusCode.Forbidden,
            ScheduleErrors.Forbidden.Code);
        using (var cancelled = await operatorClient.PostAsync($"/api/v1/servers/{server}/schedules/{waits.Id}/cancel"))
        {
            await AccessScenario.EnsureSuccessAsync(cancelled);
        }

        // Kinds and actions the panel does not know, sent as numbers, are refused as such.
        var unknownKind = new
        {
            name = "Unknown",
            cron = "0 4 * * *",
            timeZone = "UTC",
            action = "sequence",
            validate = false,
            onlyIfRunning = false,
            enabled = true,
            steps = new[] { new { kind = 99 } },
        };
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules", unknownKind), HttpStatusCode.BadRequest,
            ScheduleErrors.StepsInvalid.Code);
        var unknownAction = new { name = "Unknown", cron = "0 4 * * *", timeZone = "UTC", action = 99, validate = false, onlyIfRunning = false, enabled = true };
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules", unknownAction), HttpStatusCode.BadRequest,
            ScheduleErrors.ActionInvalid.Code);

        // A command that sets a secret is refused in a step too, and so are steps that cannot be done.
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules",
            Steps("Secret", ScheduledConditions.None, new SequenceStep(SequenceStepKind.Command, "rcon_password hunter2"))), HttpStatusCode.BadRequest,
            ScheduleErrors.CommandSensitive.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules",
            Steps("Too short", ScheduledConditions.None, new SequenceStep(SequenceStepKind.Wait))), HttpStatusCode.BadRequest, ScheduleErrors.StepsInvalid.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules",
            new ScheduledTaskRequest("No steps", "0 4 * * *", "UTC", ScheduledAction.Sequence, null, false, false, true)), HttpStatusCode.BadRequest,
            ScheduleErrors.StepsInvalid.Code);

        // A game that does not say who plays, whose updates are not checked: no task can wait for either.
        var plain = (await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "Plain")).Server.Id;
        Assert.Equal(new ScheduleAbilities(CanBroadcast: true, KnowsPlayers: false, UpdatesChecked: false), (await ListAsync(admin, plain)).Abilities);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{plain}/schedules",
            new ScheduledTaskRequest("Restart when empty", "0 4 * * *", "UTC", ScheduledAction.Restart, null, false, false, true, OnlyIfEmpty: true)),
            HttpStatusCode.BadRequest, ScheduleErrors.PlayersNotReported.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{plain}/schedules",
            new ScheduledTaskRequest("Update", "0 4 * * *", "UTC", ScheduledAction.Update, null, false, false, true, OnlyIfUpdateAvailable: true)),
            HttpStatusCode.BadRequest, ScheduleErrors.UpdateChecksUnavailable.Code);
    }

    private static ScheduledTaskRequest Steps(string name, ScheduledConditions conditions, params SequenceStep[] steps) =>
        new(name, "0 4 1 1 *", "UTC", ScheduledAction.Sequence, null, false, conditions.OnlyIfRunning, true,
            [.. steps.Select(s => new SequenceStepRequest(s.Kind, s.Text, s.Seconds, s.Validate, s.OnlyIfWasRunning, s.Always))], conditions.OnlyIfEmpty,
            conditions.OnlyIfUpdateAvailable);

    /// <summary>Moves the steps on, their waits as long as they are, until the run waits.</summary>
    private async Task WaitingAsync(PanelClient admin, Guid server, Guid task, Guid run)
    {
        ScheduledRunView? view = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await factory.Services.GetRequiredService<ServerSequences>().RunDueAsync(DateTimeOffset.UtcNow, Ct);
            view = await RunAsync(admin, server, task, run);
            return view.StepKind == SequenceStepKind.Wait;
        }, () => $"waiting (last: {view?.Status} step {view?.Step}/{view?.Steps} {view?.StepKind} {view?.ErrorCode})");
    }

    /// <summary>Moves the steps on (their waits pass at once) until the run ended.</summary>
    private async Task<ScheduledRunView> EndedAsync(PanelClient admin, Guid server, Guid task, Guid run)
    {
        ScheduledRunView? view = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await SequenceDriver.DriveAsync(factory);
            view = await RunAsync(admin, server, task, run);
            return view.Status != ScheduledRunStatus.Running;
        }, () => $"the run ended (last: {view?.Status} step {view?.Step}/{view?.Steps} {view?.StepKind} {view?.ErrorCode})");
        return view!;
    }

    private static async Task<ScheduledRunView> RunAsync(PanelClient client, Guid server, Guid task, Guid run)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/schedules/{task}/runs");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ScheduledRunList>(AccessScenario.Json, Ct))!.Runs.Single(r => r.Id == run);
    }

    private static async Task<Guid> RunningCssAsync(PanelClient admin, InProcessAgent agent, string ports)
    {
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", ports);
        var server = await GameServerTests.InstalledAsync(admin, agent, "css");
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        return server;
    }

    private static async Task<ScheduledTaskView> CreateAsync(PanelClient client, Guid server, ScheduledTaskRequest request)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{server}/schedules", request);
        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScheduledTaskView>(AccessScenario.Json, Ct))!;
    }

    private static async Task<ScheduledTaskList> ListAsync(PanelClient client, Guid server)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/schedules");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ScheduledTaskList>(AccessScenario.Json, Ct))!;
    }

    private static async Task<ScheduledRunView> RunNowAsync(PanelClient client, Guid server, Guid task)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{server}/schedules/{task}/run");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ScheduledRunView>(AccessScenario.Json, Ct))!;
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
        var created = await NodeApi.CreateAsync(admin, $"scheduled-steps-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
