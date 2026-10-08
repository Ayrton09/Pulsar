using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>A task whose operation the node refuses fails at once (and so notifies), like a refused start.</summary>
public sealed class ScheduledRefusalTests(ScheduledRefusalTests.RefusingFactory factory) : IClassFixture<ScheduledRefusalTests.RefusingFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ARefusedBackupOrUpdateFailsTheRun()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "schedules-refused");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        // A game with update steps.
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "28015-28030");
        var template = await GameServerTests.TemplateAsync(admin, "rust");
        Guid server;
        using (var create = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node.Node.Id, template.Template.Id, "rust", null)))
        {
            await AccessScenario.EnsureSuccessAsync(create);
            server = (await create.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        }

        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");

        foreach (var action in new[] { ScheduledAction.Backup, ScheduledAction.Update })
        {
            ScheduledTaskView task;
            using (var response = await admin.PostAsync($"/api/v1/servers/{server}/schedules",
                new ScheduledTaskRequest($"Refused {action}", "0 4 1 1 *", "UTC", action, null, false, false, true)))
            {
                await AccessScenario.EnsureSuccessAsync(response);
                task = (await response.Content.ReadFromJsonAsync<ScheduledTaskView>(AccessScenario.Json, Ct))!;
            }

            using var run = await admin.PostAsync($"/api/v1/servers/{server}/schedules/{task.Id}/run");
            await AccessScenario.EnsureSuccessAsync(run);
            var view = (await run.Content.ReadFromJsonAsync<ScheduledRunView>(AccessScenario.Json, Ct))!;

            Assert.Equal(ScheduledRunStatus.Failed, view.Status);
            Assert.Equal("test.refused", view.ErrorCode);

            // Stored as failed too (not only shown so from its operation): that is what notifies.
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var stored = await db.ScheduledTaskRuns.AsNoTracking().SingleAsync(r => r.Id == new ScheduledRunId(view.Id), Ct);
            Assert.Equal(ScheduledRunStatus.Failed, stored.Status);
            Assert.Equal("test.refused", stored.ErrorCode);
            Assert.NotNull(stored.OperationId);
        }
    }

    /// <summary>The panel, with nodes that refuse backups and updates.</summary>
    public sealed class RefusingFactory : PulsarApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
                services.Remove(original);
                services.AddSingleton<IAgentConnections>(sp => new Refusing((IAgentConnections)original.ImplementationFactory!(sp)));
            });
        }

        private sealed class Refusing(IAgentConnections inner) : IAgentConnections
        {
            public AgentConnectionInfo? Find(NodeId node) => inner.Find(node);

            public void Disconnect(NodeId node) => inner.Disconnect(node);

            public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

            public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
                command is CreateBackupCommand or UpdateServerCommand
                    ? Task.FromResult<CommandAck?>(new CommandAck(Guid.NewGuid(), AckStatus.Rejected, "test.refused", "Refused by the test."))
                    : inner.SendAsync(node, command, actor, cancellationToken, operationId);
        }
    }
}
