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
/// Phase 8 through the panel and the real agent core: tasks run at their local time as the schedule (audited), runs
/// missed while the panel was down are skipped rather than done late, and a task never does what its author could not.
/// </summary>
public sealed class ServerScheduleTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const string BuenosAires = "America/Argentina/Buenos_Aires";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ADailyRestartRunsAtItsLocalTimeAndIsAudited()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "daily");
        var server = await RunningServerAsync(admin, agent, "Daily");
        var started = (await ServerApi.GetAsync(admin, server))!.Server.LastStartedAt;

        var task = await CreateAsync(admin, server, new ScheduledTaskRequest("Nightly restart", "0 4 * * *", BuenosAires, ScheduledAction.Restart, null,
            false, false, true));
        var due = NextLocal(4, BuenosAires);
        Assert.Equal(due, task.NextRunAt);

        // Other tests' tasks share the database: look at this task's runs only.
        await PassAsync(due - TimeSpan.FromSeconds(1));
        Assert.Empty((await RunsAsync(admin, server, task.Id)).Runs);
        await PassAsync(due + TimeSpan.FromSeconds(2));

        var run = Assert.Single((await RunsAsync(admin, server, task.Id)).Runs);
        Assert.Equal(ScheduledRunStatus.Succeeded, run.Status);
        Assert.Equal(due, run.ScheduledFor);
        await ServerApi.WaitForAsync(admin, server,
            s => s.Server.RuntimeState == ServerRuntimeState.Running && s.Server.LastStartedAt > started, "restarted");

        var listed = Assert.Single((await ListAsync(admin, server)).Tasks);
        Assert.Equal(due + TimeSpan.FromDays(1), listed.NextRunAt);
        Assert.Equal(due, listed.LastRunAt);
        Assert.Equal(run.Id, listed.LastRun?.Id);

        var restart = Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.Restarted);
        Assert.Equal(AuditActorType.Schedule, restart.ActorType);
        Assert.Equal("Nightly restart", restart.ActorDisplay);
        Assert.Null(restart.ActorUserId);
        Assert.Equal(run.Id.ToString(), restart.CorrelationId);
        Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.ScheduleCreated && a.ActorType == AuditActorType.User);
    }

    [DatabaseFact]
    public async Task RunsMissedWhileThePanelWasDownAreSkippedNotDoneLate()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "missed");
        var server = await RunningServerAsync(admin, agent, "Missed");
        var task = await CreateAsync(admin, server, new ScheduledTaskRequest("Nightly restart", "0 4 * * *", BuenosAires, ScheduledAction.Restart, null,
            false, false, true));
        var due = task.NextRunAt!.Value;

        // Back two days and four hours later: three nights missed, nothing to do now.
        await PassAsync(due + TimeSpan.FromDays(2) + TimeSpan.FromHours(4));

        var missed = Assert.Single((await RunsAsync(admin, server, task.Id)).Runs);
        Assert.Equal(ScheduledRunStatus.Skipped, missed.Status);
        Assert.Equal(ScheduleErrors.Missed.Code, missed.ErrorCode);
        Assert.Equal(3, missed.Occurrences);
        Assert.Equal(due, missed.ScheduledFor);
        Assert.Equal(due + TimeSpan.FromDays(3), Assert.Single((await ListAsync(admin, server)).Tasks).NextRunAt);
        Assert.DoesNotContain(await AuditAsync(server), a => a.Action == AuditActions.Server.Restarted);
    }

    [DatabaseFact]
    public async Task ABackupTaskFollowsItsOperationAndCanWaitForTheServerToRun()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "backup");
        var server = await InstalledServerAsync(admin, agent, "Backup");
        var task = await CreateAsync(admin, server, new ScheduledTaskRequest("Hourly backup", "0 * * * *", "UTC", ScheduledAction.Backup, null, false,
            OnlyIfRunning: true, true));

        var skipped = await RunNowAsync(admin, server, task.Id);
        Assert.Equal(ScheduledRunStatus.Skipped, skipped.Status);
        Assert.Equal(ScheduleErrors.NotRunning.Code, skipped.ErrorCode);

        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        await PassAsync(task.NextRunAt!.Value + TimeSpan.FromSeconds(1));

        ScheduledRunView? run = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            run = (await RunsAsync(admin, server, task.Id)).Runs[0];
            return run.Status == ScheduledRunStatus.Succeeded;
        }, () => $"backup run succeeded (last: {run?.Status} {run?.ErrorCode})");
        Assert.NotNull(run!.OperationId);

        await using var scope = factory.Services.CreateAsyncScope();
        var backup = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Backups.AsNoTracking()
            .SingleAsync(b => b.OperationId == new OperationId(run.OperationId.Value), Ct);
        Assert.Equal(OperationRequester.Schedule, backup.CreatedVia);
        Assert.Null(backup.CreatedBy);
        Assert.Contains(await AuditAsync(server), a => a.Action == AuditActions.Server.BackupRequested && a.ActorDisplay == "Hourly backup");
    }

    [DatabaseFact]
    public async Task ConsoleCommandsAreSentOnlyWhileTheServerRuns()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "console");
        var server = await InstalledServerAsync(admin, agent, "Console");
        var task = await CreateAsync(admin, server, new ScheduledTaskRequest("Announce", "0 4 1 1 *", "UTC", ScheduledAction.ConsoleCommand,
            "say hello", false, false, true));
        Assert.Equal("say hello", task.Command);

        Assert.Equal(ScheduledRunStatus.Skipped, (await RunNowAsync(admin, server, task.Id)).Status);

        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        Assert.Equal(ScheduledRunStatus.Succeeded, (await RunNowAsync(admin, server, task.Id)).Status);
        var sent = Assert.Single(await AuditAsync(server), a => a.Action == AuditActions.Server.ConsoleCommand);
        Assert.Equal(AuditActorType.Schedule, sent.ActorType);
        Assert.Contains("say hello", sent.Metadata, StringComparison.Ordinal);
        Assert.Equal(2, (await AuditAsync(server)).Count(a => a.Action == AuditActions.Server.ScheduleRunRequested && a.ActorType == AuditActorType.User));
    }

    [DatabaseFact]
    public async Task SchedulingAnActionNeedsThePermissionToDoIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "rights");
        var server = await InstalledServerAsync(admin, agent, "Rights");
        var request = new ScheduledTaskRequest("Nightly restart", "0 4 * * *", "UTC", ScheduledAction.Restart, null, false, false, true);

        using var scheduler = await scenario.SignedInWithAsync("scheduler", Permissions.Server.View, Permissions.Server.SchedulesView,
            Permissions.Server.SchedulesEdit);
        await ProblemAsync(await scheduler.PostAsync($"/api/v1/servers/{server}/schedules", request), HttpStatusCode.Forbidden,
            ScheduleErrors.Forbidden.Code);

        using var restarter = await scenario.SignedInWithAsync("restarter", Permissions.Server.View, Permissions.Server.SchedulesEdit,
            Permissions.Server.Restart);
        var task = await CreateAsync(restarter, server, request);
        // Turning it into an update needs the right to update.
        await ProblemAsync(await restarter.PutAsync($"/api/v1/servers/{server}/schedules/{task.Id}", request with { Action = ScheduledAction.Update }),
            HttpStatusCode.Forbidden, ScheduleErrors.Forbidden.Code);
        // And someone who may update but not restart cannot change what restarts.
        using var updater = await scenario.SignedInWithAsync("updater", Permissions.Server.View, Permissions.Server.SchedulesEdit,
            Permissions.Server.Install);
        await ProblemAsync(await updater.PutAsync($"/api/v1/servers/{server}/schedules/{task.Id}", request with { Action = ScheduledAction.Update }),
            HttpStatusCode.Forbidden, ScheduleErrors.Forbidden.Code);

        using var viewer = await scenario.SignedInWithAsync("viewer", Permissions.Server.View, Permissions.Server.SchedulesView);
        Assert.Single((await ListAsync(viewer, server)).Tasks);
        using (var denied = await viewer.DeleteAsync($"/api/v1/servers/{server}/schedules/{task.Id}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await EnsureAsync(await updater.DeleteAsync($"/api/v1/servers/{server}/schedules/{task.Id}"));
        Assert.Empty((await ListAsync(viewer, server)).Tasks);
    }

    [DatabaseFact]
    public async Task SchedulesAreValidatedAndPreviewed()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "valid");
        var server = await InstalledServerAsync(admin, agent, "Valid");
        var request = new ScheduledTaskRequest("Task", "0 4 * * *", "UTC", ScheduledAction.Restart, null, false, false, true);

        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules", request with { Cron = "0 4 * *" }), HttpStatusCode.BadRequest,
            ScheduleErrors.CronInvalid.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules", request with { TimeZone = "Eastern Standard Time" }),
            HttpStatusCode.BadRequest, ScheduleErrors.TimeZoneInvalid.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules", request with { Action = ScheduledAction.ConsoleCommand }),
            HttpStatusCode.BadRequest, ScheduleErrors.CommandRequired.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/schedules", request with { Action = ScheduledAction.Start, OnlyIfRunning = true }),
            HttpStatusCode.BadRequest, ScheduleErrors.OnlyIfRunningUnsupported.Code);

        using var preview = await admin.GetAsync(
            $"/api/v1/servers/{server}/schedules/preview?cron={Uri.EscapeDataString("30 6 * * 1")}&timeZone={Uri.EscapeDataString(BuenosAires)}");
        await EnsureAsync(preview);
        var occurrences = (await preview.Content.ReadFromJsonAsync<SchedulePreview>(AccessScenario.Json, Ct))!.Occurrences;
        Assert.Equal(ServerSchedules.PreviewCount, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal((DayOfWeek.Monday, 9, 30), (o.UtcDateTime.DayOfWeek, o.UtcDateTime.Hour, o.UtcDateTime.Minute)));
        await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/schedules/preview?cron=nope&timeZone=UTC"), HttpStatusCode.BadRequest,
            ScheduleErrors.CronInvalid.Code);

        // Disabled: no next run, and the scheduler leaves it alone.
        var disabled = await CreateAsync(admin, server, request with { Enabled = false });
        Assert.Null(disabled.NextRunAt);
        await PassAsync(DateTimeOffset.UtcNow + TimeSpan.FromDays(2));
        Assert.Empty((await RunsAsync(admin, server, disabled.Id)).Runs);
    }

    [DatabaseFact]
    public async Task TwoSchedulersNeverRunATaskTwiceAndAbandonedRunsFail()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "twice");
        var server = await RunningServerAsync(admin, agent, "Twice");
        var task = await CreateAsync(admin, server, new ScheduledTaskRequest("Nightly restart", "0 4 * * *", "UTC", ScheduledAction.Restart, null,
            false, false, true));
        var due = task.NextRunAt!.Value + TimeSpan.FromSeconds(1);

        await Task.WhenAll(PassAsync(due), PassAsync(due), PassAsync(due));

        Assert.Single((await RunsAsync(admin, server, task.Id)).Runs);

        // A run a stopped panel left running fails at the next pass.
        ScheduledRunId abandoned;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var row = await db.ScheduledTasks.SingleAsync(t => t.Id == new ScheduledTaskId(task.Id), Ct);
            var run = row.RunNow(due);
            abandoned = run.Id;
            db.ScheduledTaskRuns.Add(run);
            await db.SaveChangesAsync(Ct);
        }

        await PassAsync(due + Scheduler.InterruptedAfter + TimeSpan.FromSeconds(1));
        var failed = (await RunsAsync(admin, server, task.Id)).Runs.Single(r => r.Id == abandoned.Value);
        Assert.Equal(ScheduledRunStatus.Failed, failed.Status);
        Assert.Equal(ScheduleErrors.Interrupted.Code, failed.ErrorCode);
    }

    private async Task<SchedulerPass> PassAsync(DateTimeOffset now) =>
        await factory.Services.GetRequiredService<Scheduler>().RunDueAsync(now, Ct);

    /// <summary>The next <paramref name="hour"/>:00 in <paramref name="zone"/>, as the panel computes it now.</summary>
    private static DateTimeOffset NextLocal(int hour, string zone)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var candidate = new DateTimeOffset(local.Year, local.Month, local.Day, hour, 0, 0, local.Offset);
        return (candidate > local ? candidate : candidate.AddDays(1)).ToUniversalTime();
    }

    private static async Task<ScheduledTaskView> CreateAsync(PanelClient client, Guid server, ScheduledTaskRequest request)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{server}/schedules", request);
        await EnsureAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScheduledTaskView>(AccessScenario.Json, Ct))!;
    }

    private static async Task<ScheduledTaskList> ListAsync(PanelClient client, Guid server)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/schedules");
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<ScheduledTaskList>(AccessScenario.Json, Ct))!;
    }

    private static async Task<ScheduledRunList> RunsAsync(PanelClient client, Guid server, Guid task)
    {
        using var response = await client.GetAsync($"/api/v1/servers/{server}/schedules/{task}/runs");
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<ScheduledRunList>(AccessScenario.Json, Ct))!;
    }

    private static async Task<ScheduledRunView> RunNowAsync(PanelClient client, Guid server, Guid task)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{server}/schedules/{task}/run");
        await EnsureAsync(response);
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

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
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
        var created = await NodeApi.CreateAsync(admin, $"schedules-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private static async Task<Guid> InstalledServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }

    private static async Task<Guid> RunningServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var server = await InstalledServerAsync(admin, agent, name);
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        return server;
    }
}
