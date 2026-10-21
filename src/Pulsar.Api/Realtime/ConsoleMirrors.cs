using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Realtime;

/// <summary>
/// The panel's copy of the consoles browsers watch (docs/architecture/03, "Console: flow and volume"). The first browser
/// to watch a server subscribes the panel to its agent; the output then goes to every browser watching it, and a browser
/// that starts watching later, or reconnects, gets what the copy holds after its last sequence. Output is deduplicated
/// by sequence, so subscribing again (after a reconnect of the agent, say) never shows anything twice. Nobody watching
/// for a while: the agent stops sending, and later the copy goes.
/// </summary>
internal sealed partial class ConsoleMirrors(
    IAgentConnections agents,
    IHubContext<PanelHub, IPanelClient> hub,
    TimeProvider clock,
    ILogger<ConsoleMirrors> logger)
{
    /// <summary>Characters kept per watched console (the agent keeps more; xterm shows 5,000 lines).</summary>
    public const int Capacity = 256 * 1024;

    public static readonly TimeSpan UnsubscribeAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<ServerId, Mirror> _mirrors = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ServerId, byte>> _connections = new();

    public static string Group(ServerId server) => $"console:{server.Value}";

    /// <summary>Adds the browser connection to the server's console and returns what it missed after <paramref name="fromSequence"/>.</summary>
    public async Task<ConsoleSnapshot> WatchAsync(string connectionId, ServerId server, NodeId node, long? fromSequence, CancellationToken cancellationToken)
    {
        Mirror mirror;
        bool subscribe;
        while (true)
        {
            mirror = _mirrors.GetOrAdd(server, _ => new Mirror(server, node));
            lock (mirror.Sync)
            {
                // An idle copy may have been removed (under its lock) since it was looked up: watching it would show a
                // silent console, so take the registered one (or a new one) instead.
                if (!_mirrors.TryGetValue(server, out var registered) || registered != mirror)
                {
                    continue;
                }

                mirror.Watchers.Add(connectionId);
                mirror.Generation++; // a pending unsubscribe or removal is off
                subscribe = !mirror.Subscribed;
                mirror.Subscribed = true;
                break;
            }
        }

        _connections.GetOrAdd(connectionId, _ => new()).TryAdd(server, 0);
        // First the group, then the copy: output arriving in between reaches the browser twice at most, never zero times.
        await hub.Groups.AddToGroupAsync(connectionId, Group(server), cancellationToken);
        var snapshot = mirror.Since(fromSequence);
        if (subscribe)
        {
            _ = SubscribeAsync(mirror);
        }

        return new ConsoleSnapshot(snapshot, agents.Find(node) is not null);
    }

    public async Task UnwatchAsync(string connectionId, ServerId server)
    {
        if (_connections.TryGetValue(connectionId, out var watched))
        {
            watched.TryRemove(server, out _);
        }

        await hub.Groups.RemoveFromGroupAsync(connectionId, Group(server));
        Leave(connectionId, server);
    }

    /// <summary>The browser connection is gone: it watches nothing anymore.</summary>
    public void Disconnected(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var watched))
        {
            foreach (var server in watched.Keys)
            {
                Leave(connectionId, server);
            }
        }
    }

    /// <summary>Output from an agent; ignored unless it is about one of that node's watched servers.</summary>
    public async Task ReceiveAsync(NodeId node, ServerConsoleOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var server = new ServerId(output.ServerId);
        if (!_mirrors.TryGetValue(server, out var mirror) || mirror.Node != node)
        {
            return;
        }

        // Absent in the JSON of a faulty agent: what is not an entry is no output.
        var fresh = mirror.Add([.. (output.Entries ?? []).Where(e => e is { Text: not null })]);
        if (fresh.Count > 0)
        {
            await hub.Clients.Group(Group(server)).ConsoleOutput(new ConsoleOutputView(output.ServerId, fresh));
        }
    }

    /// <summary>The consoles of <paramref name="node"/> browsers watch, for its Hello: they are subscribed again from there.</summary>
    public IReadOnlyList<ConsoleSubscription> SubscriptionsFor(NodeId node)
    {
        var subscriptions = new List<ConsoleSubscription>();
        foreach (var mirror in _mirrors.Values.Where(m => m.Node == node))
        {
            lock (mirror.Sync)
            {
                mirror.Subscribed = mirror.Watchers.Count > 0;
                if (mirror.Subscribed)
                {
                    subscriptions.Add(new ConsoleSubscription(mirror.Server.Value, mirror.Last));
                }
            }
        }

        return subscriptions;
    }

    /// <summary>The node's connection ended: its subscriptions ended with it.</summary>
    public void NodeDisconnected(NodeId node)
    {
        foreach (var mirror in _mirrors.Values.Where(m => m.Node == node))
        {
            lock (mirror.Sync)
            {
                mirror.Subscribed = false;
            }
        }
    }

    private async Task SubscribeAsync(Mirror mirror)
    {
        long? from;
        lock (mirror.Sync)
        {
            from = mirror.Last;
        }

        try
        {
            var ack = await agents.SendAsync(mirror.Node, new SubscribeConsoleCommand(mirror.Server.Value, from), null, CancellationToken.None);
            if (ack?.Status is not (AckStatus.Accepted or AckStatus.AlreadyInState))
            {
                // Offline (the next Hello subscribes it) or refused (the agent does not know the server).
                lock (mirror.Sync)
                {
                    mirror.Subscribed = false;
                }

                if (ack is not null)
                {
                    LogSubscribeRefused(mirror.Server, ack.Status, ack.ErrorCode);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (mirror.Sync)
            {
                mirror.Subscribed = false;
            }

            LogSubscribeFailed(ex, mirror.Server);
        }
    }

    private void Leave(string connectionId, ServerId server)
    {
        if (!_mirrors.TryGetValue(server, out var mirror))
        {
            return;
        }

        int generation;
        lock (mirror.Sync)
        {
            if (!mirror.Watchers.Remove(connectionId) || mirror.Watchers.Count > 0)
            {
                return;
            }

            generation = ++mirror.Generation;
        }

        _ = IdleAsync(mirror, generation);
    }

    /// <summary>A reload of the page should not cost a new backlog: the subscription waits a little before it ends.</summary>
    private async Task IdleAsync(Mirror mirror, int generation)
    {
        try
        {
            await Task.Delay(UnsubscribeAfter, clock);
            bool unsubscribe;
            lock (mirror.Sync)
            {
                if (mirror.Generation != generation)
                {
                    return;
                }

                unsubscribe = mirror.Subscribed;
                mirror.Subscribed = false;
            }

            if (unsubscribe)
            {
                await agents.SendAsync(mirror.Node, new UnsubscribeConsoleCommand(mirror.Server.Value), null, CancellationToken.None);
            }

            await Task.Delay(ForgetAfter - UnsubscribeAfter, clock);
            lock (mirror.Sync)
            {
                if (mirror.Generation == generation)
                {
                    _mirrors.TryRemove(new KeyValuePair<ServerId, Mirror>(mirror.Server, mirror));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogUnsubscribeFailed(ex, mirror.Server);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agent refused the console subscription of server {ServerId}: {Status} {ErrorCode}")]
    private partial void LogSubscribeRefused(ServerId serverId, AckStatus status, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not subscribe to the console of server {ServerId}")]
    private partial void LogSubscribeFailed(Exception exception, ServerId serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not end the console subscription of server {ServerId}")]
    private partial void LogUnsubscribeFailed(Exception exception, ServerId serverId);

    /// <summary>One server's console copy. Every field is guarded by <see cref="Sync"/>.</summary>
    internal sealed class Mirror(ServerId server, NodeId node)
    {
        private readonly LinkedList<ConsoleEntry> _entries = new();
        private int _length;

        public Lock Sync { get; } = new();

        public ServerId Server { get; } = server;

        public NodeId Node { get; } = node;

        public HashSet<string> Watchers { get; } = [];

        /// <summary>Changes whenever someone starts or stops watching (cancels idle timers).</summary>
        public int Generation { get; set; }

        /// <summary>The agent is (being) asked to send this console's output.</summary>
        public bool Subscribed { get; set; }

        /// <summary>The newest sequence received; null before any output.</summary>
        public long? Last { get; private set; }

        /// <summary>Keeps the entries newer than <see cref="Last"/> and returns them.</summary>
        public List<ConsoleEntry> Add(IReadOnlyList<ConsoleEntry> entries)
        {
            lock (Sync)
            {
                // Against the newest kept so far, this batch included: a repeat or an older entry in it is dropped too.
                var fresh = new List<ConsoleEntry>();
                foreach (var entry in entries)
                {
                    if (Last is { } last && entry.Sequence <= last)
                    {
                        continue;
                    }

                    fresh.Add(entry);
                    _entries.AddLast(entry);
                    _length += entry.Text.Length;
                    Last = entry.Sequence;
                }

                while (_length > Capacity && _entries.Count > 1)
                {
                    _length -= _entries.First!.Value.Text.Length;
                    _entries.RemoveFirst();
                }

                return fresh;
            }
        }

        /// <summary>The entries after <paramref name="after"/>, with a gap first when the copy no longer reaches back that far.</summary>
        public List<ConsoleEntry> Since(long? after)
        {
            lock (Sync)
            {
                if (_entries.First is not { } first)
                {
                    return [];
                }

                var result = new List<ConsoleEntry>();
                if (after is { } last && last < first.Value.Sequence - 1)
                {
                    result.Add(new ConsoleEntry(first.Value.Sequence - 1, first.Value.At, "", IsGap: true));
                }

                result.AddRange(_entries.Where(e => after is not { } a || e.Sequence > a));
                return result;
            }
        }
    }
}
