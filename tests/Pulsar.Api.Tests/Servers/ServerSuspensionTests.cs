using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Suspended servers (Pulsar 1.0.24): stopped, started by nobody, and reached only by those who may suspend them, whatever
/// else the others hold, until they are unsuspended.
/// </summary>
public sealed class ServerSuspensionTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ASuspendedServerStopsAndOnlyWhoeverMaySuspendItReachesIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "suspension");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27140-27145");
        using var user = await scenario.SignedInWithAsync("suspension-user", Permissions.Server.View, Permissions.Server.Start, Permissions.Server.Stop,
            Permissions.Server.ConsoleWrite, Permissions.Server.FilesView, Permissions.Server.FilesEdit, Permissions.Server.SchedulesView,
            Permissions.Server.BackupsCreate);

        // Its users cannot lift it, nor suspend it.
        await ProblemAsync(await user.PostAsync($"/api/v1/servers/{server}/suspend", new SuspendServerRequest(null)), HttpStatusCode.Forbidden, null);

        using (var suspended = await admin.PostAsync($"/api/v1/servers/{server}/suspend", new SuspendServerRequest("  Unpaid invoice ")))
        {
            await AccessScenario.EnsureSuccessAsync(suspended);
            var details = (await suspended.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
            Assert.Equal("Unpaid invoice", details.Server.Suspension?.Reason);
        }

        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped by the suspension");

        // Its page says so; everything else of it is closed to them, the console's live output included.
        var seen = (await ServerApi.GetAsync(user, server))!;
        Assert.Equal("Unpaid invoice", seen.Server.Suspension?.Reason);
        await ProblemAsync(await user.PostAsync($"/api/v1/servers/{server}/start"), HttpStatusCode.Conflict, ServerErrors.Suspended.Code);
        await ProblemAsync(await user.PostAsync($"/api/v1/servers/{server}/console", new ConsoleCommandRequest("say hi")), HttpStatusCode.Conflict,
            ServerErrors.Suspended.Code);
        await ProblemAsync(await user.GetAsync($"/api/v1/servers/{server}/files"), HttpStatusCode.Conflict, ServerErrors.Suspended.Code);
        await ProblemAsync(await user.GetAsync($"/api/v1/servers/{server}/schedules"), HttpStatusCode.Conflict, ServerErrors.Suspended.Code);
        await ProblemAsync(await user.PostAsync($"/api/v1/servers/{server}/backups", new { }), HttpStatusCode.Conflict, ServerErrors.Suspended.Code);

        // Whoever may suspend it still looks at its files, but nobody starts it until it is unsuspended.
        using (var files = await admin.GetAsync($"/api/v1/servers/{server}/files"))
        {
            await AccessScenario.EnsureSuccessAsync(files);
        }

        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/start"), HttpStatusCode.Conflict, ServerErrors.Suspended.Code);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/install"), HttpStatusCode.Conflict, ServerErrors.Suspended.Code);

        // Its node no longer starts it by itself, nor after a crash.
        var spec = agent.StoredSpec(server);
        Assert.Equal((false, false), (spec.AutoStart, spec.RestartOnCrash));

        using (var lifted = await admin.PostAsync($"/api/v1/servers/{server}/unsuspend"))
        {
            await AccessScenario.EnsureSuccessAsync(lifted);
            Assert.Null((await lifted.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Suspension);
        }

        spec = agent.StoredSpec(server);
        Assert.Equal((true, true), (spec.AutoStart, spec.RestartOnCrash));
        await ServerApi.CommandAsync(user, server, "start");
        await ServerApi.WaitForAsync(user, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running again");
        await ServerApi.CommandAsync(admin, server, "stop");

        var audit = await AuditAsync(server);
        var entry = Assert.Single(audit, a => a.Action == AuditActions.Server.Suspended);
        using var metadata = JsonDocument.Parse(entry.Metadata!);
        Assert.Equal("Unpaid invoice", metadata.RootElement.GetProperty("reason").GetString());
        Assert.Single(audit, a => a.Action == AuditActions.Server.Unsuspended);
    }

    [DatabaseFact]
    public async Task ASuspendedServerItsNodeRunsAnywayIsStoppedAndItsTasksAreSkipped()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "suspension-pass");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27146-27151");

        // Suspended while its node did not hear it (away, say): the game still runs.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            Assert.True((await db.Servers.SingleAsync(s => s.Id == new ServerId(server), Ct)).Suspend(null, DateTimeOffset.UtcNow).IsSuccess);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(ServerRuntimeState.Running, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ServerManagement>().StopSuspendedAsync(Ct));
        }

        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped by the suspension pass");
        var spec = agent.StoredSpec(server);
        Assert.False(spec.AutoStart);

        // A task due on it is skipped, saying why.
        using (var created = await admin.PostAsync($"/api/v1/servers/{server}/schedules",
                   new ScheduledTaskRequest("Morning start", "0 6 * * *", "UTC", ScheduledAction.Start, null, false, false, true)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            var task = (await created.Content.ReadFromJsonAsync<JsonElement>(AccessScenario.Json, Ct)).GetProperty("id").GetGuid();
            using var ran = await admin.PostAsync($"/api/v1/servers/{server}/schedules/{task}/run");
            await AccessScenario.EnsureSuccessAsync(ran);
            await InProcessAgent.EventuallyAsync(async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ScheduledTaskRuns.AsNoTracking()
                    .AnyAsync(r => r.TaskId == new ScheduledTaskId(task) && r.Status == ScheduledRunStatus.Skipped && r.ErrorCode == ScheduleErrors.Suspended.Code, Ct);
            }, "the task skipped as suspended");
        }

        Assert.Equal(ServerRuntimeState.Offline, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);
    }

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.ServerId == new ServerId(server)).ToListAsync(Ct);
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
