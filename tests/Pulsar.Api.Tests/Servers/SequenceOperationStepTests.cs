using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Scheduling;
using Pulsar.Contracts;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// A step of a run that starts an operation (a backup, an update) is recorded as following it in the save that records
/// the operation, before its command goes out: a panel stopped in between finds the step following it at the next pass,
/// and never asks for a second one.
/// </summary>
public sealed class SequenceOperationStepTests(WatchedCommandsApiFactory factory) : IClassFixture<WatchedCommandsApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ABackupStepFollowsItsOperationBeforeItsCommandIsSent()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "sequence-operation-step");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27150-27155");
        var server = await GameServerTests.InstalledAsync(admin, agent, "css");

        using var response = await admin.PostAsync($"/api/v1/servers/{server}/schedules", new ScheduledTaskRequest("Backup", "0 4 1 1 *", "UTC",
            ScheduledAction.Sequence, null, false, false, true, [new SequenceStepRequest(SequenceStepKind.Backup)],
            false, false));
        await AccessScenario.EnsureSuccessAsync(response);
        var task = (await response.Content.ReadFromJsonAsync<ScheduledTaskView>(AccessScenario.Json, Ct))!;

        var seen = new List<(Guid? Operation, Guid? Followed)>();
        factory.BeforeSend = async (command, operation) =>
        {
            if (command is CreateBackupCommand)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var sequence = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().ServerSequences.AsNoTracking()
                    .SingleAsync(s => s.ServerId == new Pulsar.Domain.Common.ServerId(server) && s.Status == SequenceStatus.Running, Ct);
                seen.Add((operation, sequence.StepBegun ? sequence.OperationId?.Value : null));
            }
        };
        try
        {
            using var started = await admin.PostAsync($"/api/v1/servers/{server}/schedules/{task.Id}/run");
            await AccessScenario.EnsureSuccessAsync(started);
            var run = (await started.Content.ReadFromJsonAsync<ScheduledRunView>(AccessScenario.Json, Ct))!;
            ScheduledRunView? view = null;
            await InProcessAgent.EventuallyAsync(async () =>
            {
                await SequenceDriver.DriveAsync(factory);
                using var runs = await admin.GetAsync($"/api/v1/servers/{server}/schedules/{task.Id}/runs");
                view = (await runs.Content.ReadFromJsonAsync<ScheduledRunList>(AccessScenario.Json, Ct))!.Runs.Single(r => r.Id == run.Id);
                return view.Status != ScheduledRunStatus.Running;
            }, () => $"the run ended (last: {view?.Status} {view?.ErrorCode})");
            Assert.Equal(ScheduledRunStatus.Succeeded, view!.Status);
        }
        finally
        {
            factory.BeforeSend = null;
        }

        var (operation, followed) = Assert.Single(seen);
        Assert.NotNull(operation);
        Assert.Equal(operation, followed);
    }
}

/// <summary>
/// A template move's step (1.0.17) is saved with the move and its provisioning. A panel of its own: the newer version it
/// ships would be a second bundled version of the game for the other tests.
/// </summary>
public sealed class TemplateStepOperationTests(WatchedCommandsApiFactory factory) : IClassFixture<WatchedCommandsApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ATemplateStepFollowsItsProvisioningSavedWithTheMove()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "step-follows");
        var server = await TemplateUpdateTests.RunningCssAsync(admin, agent, "27160-27165");
        var newer = await TemplateUpdateTests.ShipNewerAsync(factory, admin, "css");

        // Whatever the panel saved when the provisioning goes out: the move, its operation and the step following it, together.
        var seen = new List<(Guid? Operation, Guid? Followed, string Version)>();
        factory.BeforeSend = async (command, operation) =>
        {
            if (command is ProvisionServerCommand)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
                var sequence = await db.ServerSequences.AsNoTracking()
                    .SingleAsync(s => s.ServerId == new Pulsar.Domain.Common.ServerId(server) && s.Status == SequenceStatus.Running, Ct);
                var saved = await db.Servers.AsNoTracking().Where(s => s.Id == new Pulsar.Domain.Common.ServerId(server))
                    .Join(db.GameTemplates, s => s.TemplateId, t => t.Id, (_, t) => t.Version).SingleAsync(Ct);
                seen.Add((operation, sequence.StepBegun ? sequence.OperationId?.Value : null, saved));
            }
        };
        try
        {
            using (var settings = await admin.PutAsync($"/api/v1/servers/{server}/game-updates",
                       new GameUpdateSettingsRequest(GameUpdatePolicy.Notify, false, 1, null, GameUpdatePolicy.Install)))
            {
                await AccessScenario.EnsureSuccessAsync(settings);
            }

            await SequenceDriver.GameUpdatesPassAsync(factory);
            await InProcessAgent.EventuallyAsync(async () =>
            {
                await SequenceDriver.DriveAsync(factory);
                return (await SequenceDriver.GameUpdatesAsync(admin, server)).Running is { Status: not SequenceStatus.Running };
            }, "the move ended");
        }
        finally
        {
            factory.BeforeSend = null;
        }

        var (operation, followed, version) = Assert.Single(seen);
        Assert.NotNull(operation);
        Assert.Equal((operation, newer), (followed, version));
    }
}
