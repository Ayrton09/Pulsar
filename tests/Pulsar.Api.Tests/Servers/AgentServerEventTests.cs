using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>What an agent reports is checked before the panel keeps it: a bad report never breaks the panel's side.</summary>
public sealed class AgentServerEventTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ProgressThatIsNotAPercentageIsLeftOutOrClamped()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = new NodeId((await NodeApi.CreateAsync(admin, "events-progress")).Node.Id);

        foreach (var (percent, expected) in new (double, decimal?)[] { (double.NaN, null), (double.PositiveInfinity, null), (1e30, 100m), (-5, 0m), (42.123, 42.12m) })
        {
            var operation = await RunningOperationAsync(node);
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgentServerEvents>().OperationProgressAsync(node,
                new OperationProgress(operation.Value, "downloading", percent, null), Ct);

            Assert.Equal(expected, factory.Services.GetRequiredService<OperationProgressCache>().Get(operation.Value)?.Percent);
        }
    }

    [DatabaseFact]
    public async Task ProgressWithoutAStageIsKeptWithAnEmptyOne()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = new NodeId((await NodeApi.CreateAsync(admin, "events-no-stage")).Node.Id);
        var operation = await RunningOperationAsync(node);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AgentServerEvents>().OperationProgressAsync(node,
                new OperationProgress(operation.Value, null!, 10, null), Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Operations.AsNoTracking().SingleAsync(o => o.Id == operation, Ct);
            Assert.Equal(string.Empty, stored.ProgressStage);
            Assert.Equal(10m, stored.ProgressPercent);
        }
    }

    [DatabaseFact]
    public async Task TimesAreTheirsToStampNotTheAgentsClocks()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "events-clock");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        var server = (await ServerApi.CreateAsync(admin, created.Node.Id, "Skewed clock")).Server.Id;
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        var before = DateTimeOffset.UtcNow;
        long sequence;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            sequence = (await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Servers.AsNoTracking().SingleAsync(s => s.Id == new ServerId(server), Ct))
                .StateSequence;
        }

        // An agent whose clock is three hours ahead.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AgentServerEvents>().StateChangedAsync(new NodeId(created.Node.Id),
                new ServerStateChanged(server, RuntimeState.Starting, RuntimeState.Offline, before.AddHours(3), sequence + 1, null, StateChangeReason.Requested, null), Ct);
        }

        var summary = (await ServerApi.GetAsync(admin, server))!.Server;
        Assert.InRange(summary.LastStartedAt!.Value, before, DateTimeOffset.UtcNow);
    }

    [DatabaseFact]
    public async Task HelloForgetsTheProgressOfWhatItInterruptsAndTakesTheFirstOfRepeatedServers()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = new NodeId((await NodeApi.CreateAsync(admin, "events-hello")).Node.Id);
        var operation = await RunningOperationAsync(node);
        var cache = factory.Services.GetRequiredService<OperationProgressCache>();
        cache.Update(operation.Value, "downloading", 50);
        var unknown = Guid.NewGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // The same server twice: the first counts (a duplicate must not keep the node from being reconciled).
            await scope.ServiceProvider.GetRequiredService<AgentServerEvents>().ReconcileAsync(node,
                [new ServerSnapshot(unknown, RuntimeState.Offline, 1, null, null), new ServerSnapshot(unknown, RuntimeState.Running, 2, null, null)], [], Ct);
        }

        Assert.Null(cache.Get(operation.Value));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Operations.AsNoTracking().SingleAsync(o => o.Id == operation, Ct);
            Assert.Equal(OperationStatus.Interrupted, stored.Status);
        }
    }

    [DatabaseFact]
    public async Task ABackupWithAnImplausibleResultFails()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "events-backup");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        var server = (await ServerApi.CreateAsync(admin, created.Node.Id, "Counts")).Server.Id;
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        var node = new NodeId(created.Node.Id);

        foreach (var result in new[] { new BackupResult(-1, 3, 0, new string('a', 64)), new BackupResult(10, -1, 0, new string('a', 64)),
            new BackupResult(10, 3, -1, new string('a', 64)), new BackupResult(10, 3, 0, null!) })
        {
            BackupId backupId;
            Operation operation;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
                var row = await db.Servers.SingleAsync(s => s.Id == new ServerId(server), Ct);
                operation = Operation.Create(OperationType.BackupCreate, node, row.Id, null, OperationRequester.System, DateTimeOffset.UtcNow);
                operation.Started(DateTimeOffset.UtcNow);
                var backup = Backup.Create(row, "counts", operation, null, OperationRequester.System, DateTimeOffset.UtcNow).Value!;
                backupId = backup.Id;
                db.Operations.Add(operation);
                db.Backups.Add(backup);
                await db.SaveChangesAsync(Ct);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AgentServerEvents>().OperationCompletedAsync(node,
                    new OperationCompleted(operation.Id.Value, server, OperationOutcome.Succeeded, null, null, result), Ct);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var stored = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Backups.AsNoTracking().SingleAsync(b => b.Id == backupId, Ct);
                Assert.Equal(BackupStatus.Failed, stored.Status);
            }
        }
    }

    private async Task<OperationId> RunningOperationAsync(NodeId node)
    {
        var operation = Operation.Create(OperationType.ServerInstall, node, null, null, OperationRequester.System, DateTimeOffset.UtcNow);
        operation.Started(DateTimeOffset.UtcNow);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        db.Operations.Add(operation);
        await db.SaveChangesAsync(Ct);
        return operation.Id;
    }
}
