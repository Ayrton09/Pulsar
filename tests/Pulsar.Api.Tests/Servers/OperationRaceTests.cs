using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// An agent can finish a quick operation and report it before the panel has recorded that the agent accepted it (the
/// ack and the result travel separately). The result must win, and the request that started the operation must not fail.
/// </summary>
public sealed class OperationRaceTests(OperationRaceTests.ResultFirstFactory factory) : IClassFixture<OperationRaceTests.ResultFirstFactory>
{
    [DatabaseFact]
    public async Task AResultThatArrivesBeforeTheAckIsKept()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "race");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");

        // Provisioning and install each finish before their ack is handed back (see ResultFirstConnections).
        var created = await ServerApi.CreateAsync(admin, node.Node.Id, "Quick");

        var installed = await ServerApi.WaitForAsync(admin, created.Server.Id,
            s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        Assert.All(installed.RecentOperations, o => Assert.Equal(OperationStatus.Succeeded, o.Status));
        // The install's result makes the server installed while its ack is still held back: wait for both to be counted.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Raced >= 2), () => $"both acks held back (so far {factory.Raced})");
    }

    /// <summary>The panel, with commands whose ack is held back until the operation's result has been stored.</summary>
    public sealed class ResultFirstFactory : PulsarApiFactory
    {
        private int _raced;

        public int Raced => _raced;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
                services.Remove(original);
                services.AddSingleton<IAgentConnections>(sp => new ResultFirstConnections(
                    (IAgentConnections)original.ImplementationFactory!(sp), sp.GetRequiredService<IServiceScopeFactory>(), this));
            });
        }

        private sealed class ResultFirstConnections(IAgentConnections inner, IServiceScopeFactory scopes, ResultFirstFactory owner) : IAgentConnections
        {
            public AgentConnectionInfo? Find(NodeId node) => inner.Find(node);

            public void Disconnect(NodeId node) => inner.Disconnect(node);

            public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

            public async Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken,
                Guid? operationId = null)
            {
                var ack = await inner.SendAsync(node, command, actor, cancellationToken, operationId);
                if (operationId is { } id && ack?.Status == AckStatus.Accepted)
                {
                    await InProcessAgent.EventuallyAsync(async () =>
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
                        return await db.Operations.AnyAsync(o => o.Id == new OperationId(id)
                            && o.Status != OperationStatus.Queued && o.Status != OperationStatus.Running, cancellationToken);
                    }, "the operation's result stored before its ack");
                    Interlocked.Increment(ref owner._raced);
                }

                return ack;
            }
        }
    }
}
