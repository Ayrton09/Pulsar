using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// The Phase 3 flow through the panel and the real agent core: a server is created (provisioned and installed by its
/// agent), started, stopped, restarted, killed and deleted; reconnections reconcile the panel with what the agent sees.
/// </summary>
public sealed class ServerLifecycleTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AServerIsCreatedStartedStoppedRestartedKilledAndDeletedFromThePanel()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "lifecycle");

        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "Test server");
        Assert.Equal(InstallState.Installing, created.Server.InstallState);
        Assert.Equal(OperationType.ServerProvision, created.Server.ActiveOperation?.Type);

        var installed = await ServerApi.WaitForAsync(admin, created.Server.Id,
            s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "provisioned and installed");
        Assert.Equal([OperationType.ServerInstall, OperationType.ServerProvision], installed.RecentOperations.Select(o => o.Type));
        Assert.All(installed.RecentOperations, o => Assert.Equal(OperationStatus.Succeeded, o.Status));
        Assert.True(File.Exists(Path.Combine(agent.ServersRoot, created.Server.ShortId, "bin", "Pulsar.TestGameServer.exe")));

        Assert.Equal(AckStatus.Accepted, (await ServerApi.CommandAsync(admin, created.Server.Id, "start")).Status);
        var running = await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        Assert.Equal(DesiredState.Running, running.Server.DesiredState);
        Assert.Equal(ServerStateReason.Ready, running.Server.LastStateReason);
        Assert.Equal(AckStatus.AlreadyInState, (await ServerApi.CommandAsync(admin, created.Server.Id, "start")).Status);

        await ServerApi.CommandAsync(admin, created.Server.Id, "stop");
        var stopped = await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped");
        Assert.Equal(DesiredState.Stopped, stopped.Server.DesiredState);
        Assert.Equal(0, stopped.Server.LastExitCode);

        await ServerApi.CommandAsync(admin, created.Server.Id, "restart");
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running after restart");

        Assert.Equal(AckStatus.Accepted, (await ServerApi.CommandAsync(admin, created.Server.Id, "kill")).Status);
        var killed = await ServerApi.WaitForAsync(admin, created.Server.Id,
            s => s.Server.RuntimeState == ServerRuntimeState.Offline && s.Server.LastStateReason == ServerStateReason.Killed, "killed");
        Assert.Equal(DesiredState.Stopped, killed.Server.DesiredState);
        Assert.All(agent.Processes.Started, p => Assert.False(p.IsAlive));

        using (var delete = await admin.Http.DeleteAsync(new Uri($"/api/v1/servers/{created.Server.Id}", UriKind.Relative), Ct))
        {
            await AccessScenario.EnsureSuccessAsync(delete);
        }

        await InProcessAgent.EventuallyAsync(async () => await ServerApi.GetAsync(admin, created.Server.Id) is null, "server deleted");
        Assert.False(Directory.Exists(Path.Combine(agent.ServersRoot, created.Server.ShortId)));
        Assert.Empty(agent.Servers!.Snapshots());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        string[] expected = ["server.created", "server.start", "server.stop", "server.restart", "server.kill", "server.delete_requested", "server.deleted"];
        var actions = await db.AuditLog.Where(e => e.ServerId == new ServerId(created.Server.Id)).OrderBy(e => e.OccurredAt).Select(e => e.Action)
            .ToListAsync(Ct);
        Assert.Equal(expected.Order(), actions.Distinct().Order());
    }

    [DatabaseFact]
    public async Task AGameThatCrashesIsRestartedAndTheExitCodeIsShown()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "crash");
        var server = await InstalledServerAsync(admin, agent, "Crashy");
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");

        agent.Processes.Started[^1].Exit(3);

        var crashed = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Crashed, "crashed");
        Assert.Equal(3, crashed.Server.LastExitCode);
        Assert.Equal(ServerStateReason.Crashed, crashed.Server.LastStateReason);
        Assert.Equal(DesiredState.Running, crashed.Server.DesiredState);
    }

    [DatabaseFact]
    public async Task AQuitTypedInTheGameIsAStopTheUserDidNotHaveToAskFor()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "quit");
        var server = await InstalledServerAsync(admin, agent, "Quitter");
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");

        agent.Processes.Started[^1].Exit(0);

        var offline = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "offline");
        Assert.Equal(ServerStateReason.ExitedByItself, offline.Server.LastStateReason);
        Assert.Equal(DesiredState.Stopped, offline.Server.DesiredState);
    }

    [DatabaseFact]
    public async Task AServerRunningWhenTheAgentStopsComesBackWithTheAgent()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "resume");
        var server = await InstalledServerAsync(admin, agent, "Survivor");
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");

        await agent.StopAsync();
        var stopped = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped with the agent");
        Assert.Equal(ServerStateReason.AgentStopping, stopped.Server.LastStateReason);
        Assert.Equal(DesiredState.Running, stopped.Server.DesiredState);

        await agent.StartAsync();

        var back = await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running again");
        Assert.True(back.Server.NodeConnected);
    }

    [DatabaseFact]
    public async Task TheHelloReconcilesStatesAndInterruptsOperationsTheAgentDoesNotHave()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "reconcile");
        var serverId = await InstalledServerAsync(admin, agent, "Reconciled");
        var nodeId = new NodeId(agent.Config!.NodeId);

        // What the panel may believe after a crash of its own: the server running and an install in progress.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var server = await db.Servers.SingleAsync(s => s.Id == new ServerId(serverId), Ct);
            var operation = Operation.Create(OperationType.ServerInstall, nodeId, server.Id, null, OperationRequester.System, DateTimeOffset.UtcNow);
            Assert.True(server.BeginOperation(operation, DateTimeOffset.UtcNow).IsSuccess);
            server.Observe(ServerRuntimeState.Running, ServerStateReason.Ready, null, long.MaxValue, DateTimeOffset.UtcNow, snapshot: false);
            db.Operations.Add(operation);
            operation.Started(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        factory.Services.GetRequiredService<IAgentConnections>().Disconnect(nodeId);

        var reconciled = await ServerApi.WaitForAsync(admin, serverId,
            s => s.Server.RuntimeState == ServerRuntimeState.Offline && s.Server.LastStateReason == ServerStateReason.Reconciled, "reconciled");
        Assert.Contains(reconciled.RecentOperations, o => o.Status == OperationStatus.Interrupted && o.ErrorCode == "operation.interrupted");
    }

    [DatabaseFact]
    public async Task CommandsNeedAConnectedNode()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "offline");
        var server = await InstalledServerAsync(admin, agent, "Lonely");
        await agent.StopAsync();
        await InProcessAgent.EventuallyAsync(async () => !(await NodeApi.GetAsync(admin, agent.Config!.NodeId)).Node.Connected, "disconnected");

        using var start = await admin.PostAsync($"/api/v1/servers/{server}/start");

        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Contains("node.not_connected", await start.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var deleteNode = await admin.Http.DeleteAsync(new Uri($"/api/v1/nodes/{agent.Config!.NodeId}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Conflict, deleteNode.StatusCode);
        Assert.Contains("node.has_servers", await deleteNode.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task AnAgentCannotReportOnAnotherNodesServers()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "owner");
        var server = await InstalledServerAsync(admin, agent, "Owned");
        var intruderNode = await NodeApi.CreateAsync(admin, "intruder");
        await using var intruder = new TestAgent(factory);
        await intruder.EnrollAsync(intruderNode.Enrollment.Token, intruderNode.Enrollment.PanelKeyFingerprint);
        var token = await intruder.AuthenticateAsync();
        await intruder.ConnectAsync(token.AccessToken);

        await intruder.Connection!.InvokeAsync(nameof(IAgentHub.ServerStateChanged),
            new ServerStateChanged(server, RuntimeState.Crashed, RuntimeState.Offline, DateTimeOffset.UtcNow, long.MaxValue, 66, StateChangeReason.Crashed, null), Ct);

        var details = await ServerApi.GetAsync(admin, server);
        Assert.Equal(ServerRuntimeState.Offline, details!.Server.RuntimeState);
        Assert.Null(details.Server.LastExitCode);
    }

    [DatabaseFact]
    public async Task TheTestGameIsOnlyOfferedToAgentsThatHaveIt()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "without-test-game");
        await using var plain = new TestAgent(factory);
        await plain.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await plain.ConnectAsync((await plain.AuthenticateAsync()).AccessToken, AgentCapabilities.ProvisionServer);
        var template = await ServerApi.TestTemplateAsync(admin);

        using var response = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node.Node.Id, template.Id, "Nope", null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("server.template_unsupported", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"servers-{name}");
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
