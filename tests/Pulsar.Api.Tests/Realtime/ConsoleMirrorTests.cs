using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Api.Realtime;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Realtime;

/// <summary>The panel's console copy keeps each sequence once, in order (docs/architecture/03, "Console: flow and volume").</summary>
public sealed class ConsoleMirrorTests
{
    [Fact]
    public void ABatchNeverAddsASequenceTwiceNorMovesBackwards()
    {
        var mirror = new ConsoleMirrors.Mirror(new ServerId(Guid.NewGuid()), new NodeId(Guid.NewGuid()));

        Assert.Equal([5], mirror.Add([Entry(5)]).Select(e => e.Sequence));
        Assert.Equal([6], mirror.Add([Entry(6), Entry(6)]).Select(e => e.Sequence));
        Assert.Equal([7], mirror.Add([Entry(7), Entry(6)]).Select(e => e.Sequence));
        Assert.Equal(7, mirror.Last);
        Assert.Equal([8], mirror.Add([Entry(8)]).Select(e => e.Sequence));
        Assert.Equal([5, 6, 7, 8], mirror.Since(null).Select(e => e.Sequence));
    }

    [Fact]
    public async Task AServerMovedToAnotherNodeIsWatchedThereFromTheStart()
    {
        var agents = new RecordingAgents();
        var mirrors = new ConsoleMirrors(agents, new NoHub(), TimeProvider.System, NullLogger<ConsoleMirrors>.Instance);
        var server = new ServerId(Guid.NewGuid());
        var before = new NodeId(Guid.NewGuid());
        var after = new NodeId(Guid.NewGuid());
        await mirrors.WatchAsync("browser-1", server, before, null, TestContext.Current.CancellationToken);
        await agents.SubscribedAsync(before);

        // Moved by a transfer while the first browser still watches; the console is opened again.
        await mirrors.WatchAsync("browser-2", server, after, null, TestContext.Current.CancellationToken);

        Assert.Null(await agents.SubscribedAsync(after)); // from the start: not after the old node's sequences
        Assert.Equal([server.Value], mirrors.SubscriptionsFor(after).Select(s => s.ServerId));
        Assert.Empty(mirrors.SubscriptionsFor(before));
    }

    private static ConsoleEntry Entry(long sequence) => new(sequence, DateTimeOffset.UnixEpoch, $"line {sequence}\n");

    private sealed class RecordingAgents : IAgentConnections
    {
        private readonly ConcurrentDictionary<NodeId, TaskCompletionSource<long?>> _subscribed = new();

        public Task<long?> SubscribedAsync(NodeId node) => Subscription(node).Task.WaitAsync(TimeSpan.FromSeconds(10));

        public AgentConnectionInfo? Find(NodeId node) => null;

        public void Disconnect(NodeId node)
        {
        }

        public void DisconnectEnded(NodeId node)
        {
        }

        public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null)
        {
            if (command is SubscribeConsoleCommand subscribe)
            {
                Subscription(node).TrySetResult(subscribe.FromSequence);
            }

            return Task.FromResult<CommandAck?>(new CommandAck(Guid.NewGuid(), AckStatus.Accepted, null, null));
        }

        public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => Task.FromResult(0);

        private TaskCompletionSource<long?> Subscription(NodeId node) =>
            _subscribed.GetOrAdd(node, _ => new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class NoHub : IHubContext<PanelHub, IPanelClient>, IGroupManager
    {
        public IHubClients<IPanelClient> Clients => throw new NotSupportedException();

        public IGroupManager Groups => this;

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
