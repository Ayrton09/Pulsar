using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// A request that goes away (the browser closed) after the node was told to act is still audited: the node does what
/// it was told whatever happens to the request.
/// </summary>
public sealed class AuditAfterGivenUpRequestTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AConsoleCommandTheNodeRanIsAuditedEvenIfItsRequestWasGivenUp()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "console");
        var id = await InstalledServerAsync(admin, agent, "Console given up");
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<IPulsarDbContext>().Database
                .ExecuteSqlAsync($"UPDATE servers SET runtime_state = 'running' WHERE id = {id}", Ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        using var givenUp = new CancellationTokenSource();
        var connections = Answering(scope, givenUp);
        var commands = ActivatorUtilities.CreateInstance<ConsoleCommands>(scope.ServiceProvider, connections);

        var sent = await commands.SendAsync(new ServerId(id), "say hello", givenUp.Token);

        Assert.Equal(AckStatus.Accepted, sent.Value.Status);
        await AssertAuditedAsync(id, AuditActions.Server.ConsoleCommand);
    }

    [DatabaseFact]
    public async Task AFileChangeTheNodeMadeIsAuditedEvenIfItsRequestWasGivenUp()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "files");
        var id = await InstalledServerAsync(admin, agent, "Files given up");
        await using var scope = factory.Services.CreateAsyncScope();
        using var givenUp = new CancellationTokenSource();
        var files = ActivatorUtilities.CreateInstance<ServerFiles>(scope.ServiceProvider, Answering(scope, givenUp));

        var deleted = await files.DeleteAsync(new ServerId(id), "old.log", givenUp.Token);

        Assert.True(deleted.IsSuccess);
        await AssertAuditedAsync(id, AuditActions.Server.FileDeleted);
    }

    [DatabaseFact]
    public async Task AnUploadTheNodeWasToldAboutIsAuditedEvenIfItsRequestWasGivenUp()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "upload");
        var id = await InstalledServerAsync(admin, agent, "Upload given up");
        await using var scope = factory.Services.CreateAsyncScope();
        using var givenUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        // The node accepts but never claims the transfer: the request is given up while it waits.
        var connections = new InterceptedConnections(scope.ServiceProvider.GetRequiredService<IAgentConnections>(), (command, next, token) =>
            Task.FromResult<CommandAck?>(new CommandAck(Guid.CreateVersion7(), AckStatus.Accepted, null, null)));
        var files = ActivatorUtilities.CreateInstance<ServerFiles>(scope.ServiceProvider, (IAgentConnections)connections);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            files.WriteAsync(new ServerId(id), "motd.txt", new MemoryStream([1, 2, 3]), 3, overwrite: true, expectedVersion: null, givenUp.Token));

        await AssertAuditedAsync(id, AuditActions.Server.FileWritten);
    }

    /// <summary>Connections whose answer comes after the request was given up (as if the browser closed while waiting).</summary>
    private static InterceptedConnections Answering(AsyncServiceScope scope, CancellationTokenSource givenUp) =>
        new InterceptedConnections(scope.ServiceProvider.GetRequiredService<IAgentConnections>(), async (command, next, token) =>
        {
            await givenUp.CancelAsync();
            token.ThrowIfCancellationRequested(); // what a real wait does when its token was cancelled
            return new CommandAck(Guid.CreateVersion7(), AckStatus.Accepted, null, null);
        });

    private async Task AssertAuditedAsync(Guid server, string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        Assert.True(await db.AuditLog.AsNoTracking().AnyAsync(e => e.ServerId == new ServerId(server) && e.Action == action, Ct), $"{action} not audited");
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"given-up-{name}");
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
