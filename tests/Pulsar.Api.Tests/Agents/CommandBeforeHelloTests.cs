using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Application.Realtime;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// The panel's first commands can reach an agent before the answer to its Hello (an agent that missed a panel key
/// rotation gets the new keys as soon as it is registered): they must not wait on that answer, nor be judged expired on a
/// node whose clock is behind (the agent knows the panel's clock from its session's grant, before Hello).
/// </summary>
public sealed class CommandBeforeHelloTests(CommandBeforeHelloTests.Factory factory) : IClassFixture<CommandBeforeHelloTests.Factory>
{
    [DatabaseFact]
    public async Task ACommandArrivingBeforeHellosAnswerIsAcceptedOnANodeWhoseClockIsBehind()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "command-before-hello");
        // Ten minutes behind the panel: judged on its own clock, every command of the panel is from the future.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        await using var agent = new InProcessAgent(factory, clock);
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var ping = factory.SendDuringHello(new NodeId(config.NodeId), new PingCommand(null));

        await agent.StartAsync();

        Assert.Equal(AckStatus.Accepted, (await ping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))?.Status);
        // The command is answered before Hello is: the agent has its session once Hello is answered.
        await InProcessAgent.EventuallyAsync(async () => agent.CurrentSessionId is not null && (await NodeApi.GetAsync(admin, config.NodeId)).Node.Connected,
            "connection");
        var session = agent.CurrentSessionId;
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
        Assert.Equal(session, agent.CurrentSessionId);
    }

    /// <summary>The panel, sending a command to a node while its Hello is still being answered.</summary>
    public sealed class Factory : PulsarApiFactory
    {
        private readonly Lock _lock = new();
        private NodeId? _node;
        private AgentCommand? _command;
        private TaskCompletionSource<CommandAck?>? _ack;

        /// <summary>Sends <paramref name="command"/> once <paramref name="node"/>'s Hello has registered it, before answering it.</summary>
        public Task<CommandAck?> SendDuringHello(NodeId node, AgentCommand command)
        {
            lock (_lock)
            {
                _node = node;
                _command = command;
                _ack = new TaskCompletionSource<CommandAck?>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _ack.Task;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IRealtimeNotifier));
                services.Remove(original);
                services.AddSingleton<IRealtimeNotifier>(sp => new Notifier(
                    (IRealtimeNotifier)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), sp, this));
            });
        }

        /// <summary>Hello tells the browsers the node changed right after registering its connection.</summary>
        private sealed class Notifier(IRealtimeNotifier inner, IServiceProvider services, Factory factory) : IRealtimeNotifier
        {
            public void NodesChanged(IReadOnlyCollection<NodeId> nodes)
            {
                var connections = services.GetRequiredService<IAgentConnections>();
                NodeId? target = null;
                AgentCommand? command;
                TaskCompletionSource<CommandAck?>? ack;
                lock (factory._lock)
                {
                    // Authentication tells them too, before there is a connection: only Hello's notice counts.
                    command = factory._node is { } node && nodes.Contains(node) && connections.Find(node) is not null ? factory._command : null;
                    ack = factory._ack;
                    if (command is not null)
                    {
                        target = factory._node;
                        factory._node = null;
                    }
                }

                if (command is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            ack!.TrySetResult(await connections.SendAsync(target!.Value, command, null, CancellationToken.None));
                        }
                        catch (Exception ex)
                        {
                            ack!.TrySetException(ex);
                        }
                    });

                    // Hello is answered only once the command is surely on its way.
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }

                inner.NodesChanged(nodes);
            }

            public void ServersChanged(IReadOnlyCollection<ServerId> servers, IReadOnlyDictionary<ServerId, NodeId>? nodes = null) =>
                inner.ServersChanged(servers, nodes);

            public void OperationProgress(ServerId server, Guid operationId, string stage, decimal? percent) =>
                inner.OperationProgress(server, operationId, stage, percent);

            public void AppearanceChanged() => inner.AppearanceChanged();
        }
    }
}
