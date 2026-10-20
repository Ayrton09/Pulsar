using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// The agent reports on an operation it accepted (each new stage of a backup changes its row) while the panel records
/// that it started: however often those reports land, the request that started it does not fail.
/// </summary>
public sealed class OperationStartRaceTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AnOperationTheAgentKeepsReportingOnIsRecordedAsStarted()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "reports");
        var id = new ServerId(await InstalledServerAsync(admin, agent, "Busy reports"));
        await using var scope = factory.Services.CreateAsyncScope();
        Task<int>? reports = null;

        // The node accepts the backup (and is never told, so it stays to be done); from then on its row changes over and
        // over, as the agent's reports change it, for as long as it is still queued.
        var connections = new InterceptedConnections(scope.ServiceProvider.GetRequiredService<IAgentConnections>(), async (command, next, token) =>
        {
            if (command is not CreateBackupCommand)
            {
                return await next(token);
            }

            var reporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reports = Task.Run(() => ReportAsync(id, reporting), CancellationToken.None);
            await reporting.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            return new CommandAck(Guid.CreateVersion7(), AckStatus.Accepted, null, null);
        });
        var operations = ActivatorUtilities.CreateInstance<ServerOperations>(scope.ServiceProvider, (IAgentConnections)connections);
        var backups = ActivatorUtilities.CreateInstance<ServerBackups>(scope.ServiceProvider, (IAgentConnections)connections, operations);

        var created = await backups.CreateAsync(id, "while it reports", OperationRequester.System, Ct);

        Assert.True(created.IsSuccess, created.IsSuccess ? null : created.Error!.Code);
        Assert.True(await reports!.WaitAsync(TimeSpan.FromSeconds(30), Ct) >= 3, "the reports were running");
        await using var check = factory.Services.CreateAsyncScope();
        var operation = await check.ServiceProvider.GetRequiredService<IPulsarDbContext>().Operations.AsNoTracking()
            .SingleAsync(o => o.ServerId == id && o.Type == OperationType.BackupCreate, Ct);
        Assert.Equal(OperationStatus.Running, operation.Status);
        Assert.NotNull(operation.StartedAt);
    }

    /// <summary>
    /// Changes the server's queued backup operation as fast as it can until it is no longer queued (or 20 seconds went
    /// by), saying once it changed it a few times; returns how many times it did.
    /// </summary>
    private async Task<int> ReportAsync(ServerId server, TaskCompletionSource reporting)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var count = 0;
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        try
        {
            while (DateTimeOffset.UtcNow < until)
            {
                var stage = $"report {count + 1}";
                if (await db.Operations.Where(o => o.ServerId == server && o.Type == OperationType.BackupCreate && o.Status == OperationStatus.Queued)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProgressStage, stage), CancellationToken.None) == 0)
                {
                    break;
                }

                if (++count == 3)
                {
                    reporting.TrySetResult();
                }
            }

            return count;
        }
        finally
        {
            reporting.TrySetResult();
        }
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"start-race-{name}");
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
}
