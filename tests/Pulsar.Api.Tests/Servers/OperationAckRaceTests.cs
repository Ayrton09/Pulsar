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
/// The node's answer to an operation's command is saved even when the agent reports a state change of the server in
/// between: what the answer means (started, or refused and the server released) is applied again on the fresh rows.
/// </summary>
public sealed class OperationAckRaceTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseTheory]
    [InlineData(AckStatus.Accepted, OperationStatus.Running, false)]
    [InlineData(AckStatus.Busy, OperationStatus.Failed, true)]
    public async Task TheAnswerIsKeptWhenTheServersRowChangesMeanwhile(AckStatus answer, OperationStatus expected, bool serverFree)
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, $"ack-race-{answer}".ToLowerInvariant());
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        var created = await ServerApi.CreateAsync(admin, node.Node.Id, "Ack race");
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");

        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var connections = new InterceptedConnections(services.GetRequiredService<IAgentConnections>(), async (command, next, token) =>
        {
            // As if the agent had reported a state change of the server just before the panel saves its answer.
            await using var other = factory.Services.CreateAsyncScope();
            await other.ServiceProvider.GetRequiredService<IPulsarDbContext>().Database
                .ExecuteSqlAsync($"UPDATE servers SET updated_at = updated_at WHERE id = {created.Server.Id}", token);
            return answer == AckStatus.Accepted
                ? new CommandAck(Guid.CreateVersion7(), AckStatus.Accepted, null, null)
                : new CommandAck(Guid.CreateVersion7(), answer, "server.busy", "Another operation is under way.");
        });
        var operations = ActivatorUtilities.CreateInstance<ServerOperations>(services, (IAgentConnections)connections);
        var db = services.GetRequiredService<IPulsarDbContext>();
        var server = await db.Servers.SingleAsync(s => s.Id == new ServerId(created.Server.Id), Ct);
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, DateTimeOffset.UtcNow);

        await operations.RunAsync(server, operation, new CreateBackupCommand(created.Server.Id, Guid.CreateVersion7()), null, Ct);

        await using var check = factory.Services.CreateAsyncScope();
        var fresh = check.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        Assert.Equal(expected, (await fresh.Operations.AsNoTracking().SingleAsync(o => o.Id == operation.Id, Ct)).Status);
        var stored = await fresh.Servers.AsNoTracking().SingleAsync(s => s.Id == server.Id, Ct);
        Assert.Equal(serverFree, stored.ActiveOperationId is null);
    }
}
