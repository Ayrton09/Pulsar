using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>A run is deleted with its task or server: one taken before that is dropped, and the rest of its pass goes on.</summary>
public sealed class SchedulerDeletedRunTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ARunDeletedAfterItWasTakenIsSimplyDropped()
    {
        var exception = await Record.ExceptionAsync(() =>
            factory.Services.GetRequiredService<Scheduler>().ExecuteAsync(ScheduledRunId.New(), Ct));

        Assert.Null(exception);
    }

    [DatabaseFact]
    public async Task ARunDeletedWhileItsActionFailsUnexpectedlyIsDropped()
    {
        var (_, run) = await TakenRunAsync("failing", ScheduledAction.Start, null);
        // The action blows up (here: resolving the service that starts servers) just after the run was deleted.
        var scheduler = SchedulerThatDeletes(run, typeof(ServerManagement), throwAfterwards: true);

        var exception = await Record.ExceptionAsync(() => scheduler.ExecuteAsync(run, Ct));

        Assert.Null(exception);
        Assert.False(await RunExistsAsync(run));
    }

    [DatabaseFact]
    public async Task ARunDeletedWhileItsActionRanIsNotRecordedAndNothingIsThrown()
    {
        // A console command to a server that is not running ends skipped: the run is saved after the delete.
        var (_, run) = await TakenRunAsync("saved", ScheduledAction.ConsoleCommand, "say hi");
        var scheduler = SchedulerThatDeletes(run, typeof(RequestContextOverride), throwAfterwards: false);

        var exception = await Record.ExceptionAsync(() => scheduler.ExecuteAsync(run, Ct));

        Assert.Null(exception);
        Assert.False(await RunExistsAsync(run));
    }

    [DatabaseFact]
    public async Task ARunNotDeletedIsStillRecordedWhenItsActionFailsUnexpectedly()
    {
        var (_, run) = await TakenRunAsync("recorded", ScheduledAction.Start, null);
        var scheduler = SchedulerThatDeletes(run, typeof(ServerManagement), throwAfterwards: true, delete: false);

        await scheduler.ExecuteAsync(run, Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ScheduledTaskRuns.AsNoTracking().SingleAsync(r => r.Id == run, Ct);
        Assert.Equal(ScheduledRunStatus.Failed, row.Status);
        Assert.Equal("schedules.error", row.ErrorCode);
    }

    /// <summary>A task of a new server with a run taken by a scheduler that has not done it yet.</summary>
    private async Task<(Guid Server, ScheduledRunId Run)> TakenRunAsync(string name, ScheduledAction action, string? command)
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, $"deleted-run-{name}");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        var server = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, $"Deleted run {name}");
        await ServerApi.WaitForAsync(admin, server.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");

        using var response = await admin.PostAsync($"/api/v1/servers/{server.Server.Id}/schedules",
            new ScheduledTaskRequest($"Deleted run {name}", "0 4 1 1 *", "UTC", action, command, false, false, true));
        response.EnsureSuccessStatusCode();
        var task = (await response.Content.ReadFromJsonAsync<ScheduledTaskView>(AccessScenario.Json, Ct))!;

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var row = await db.ScheduledTasks.SingleAsync(t => t.Id == new ScheduledTaskId(task.Id), Ct);
        var run = row.RunNow(DateTimeOffset.UtcNow);
        db.ScheduledTaskRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        return (server.Server.Id, run.Id);
    }

    /// <summary>
    /// A scheduler whose scopes, when asked for <paramref name="hooked"/>, first delete the run (as the deletion of its task
    /// or server does while the action runs) and then, if asked, fail as an action failing unexpectedly does.
    /// </summary>
    private Scheduler SchedulerThatDeletes(ScheduledRunId run, Type hooked, bool throwAfterwards, bool delete = true) =>
        new(new HookedScopes(factory.Services.GetRequiredService<IServiceScopeFactory>(), hooked, () =>
        {
            if (delete)
            {
                using var scope = factory.Services.CreateScope();
                scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ScheduledTaskRuns.Where(r => r.Id == run).ExecuteDelete();
            }

            if (throwAfterwards)
            {
                throw new InvalidOperationException("The action failed unexpectedly.");
            }
        }), TimeProvider.System, NullLogger<Scheduler>.Instance);

    private async Task<bool> RunExistsAsync(ScheduledRunId run)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ScheduledTaskRuns.AnyAsync(r => r.Id == run, Ct);
    }

    private sealed class HookedScopes(IServiceScopeFactory inner, Type hooked, Action onResolve) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new HookedScope(inner.CreateScope(), hooked, onResolve);
    }

    private sealed class HookedScope(IServiceScope inner, Type hooked, Action onResolve) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == hooked)
            {
                onResolve();
            }

            return inner.ServiceProvider.GetService(serviceType);
        }

        public void Dispose() => inner.Dispose();
    }
}
