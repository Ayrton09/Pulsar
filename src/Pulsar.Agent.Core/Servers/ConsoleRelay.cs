using System.Collections.Concurrent;
using Pulsar.Agent.Core.Panel;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Sends the console output of the servers the panel subscribed to (docs/architecture/03, "Console: flow and volume"):
/// from the requested sequence on, in messages of up to <see cref="BatchLength"/> characters, at most one per
/// <see cref="SendInterval"/> per server, so a noisy game cannot flood the link. A subscription that falls behind
/// further than the server's buffer reaches gets a gap entry instead of the lost output. Subscriptions end with the
/// panel connection; the panel subscribes again in its answer to the next Hello.
/// </summary>
public sealed class ConsoleRelay : IDisposable
{
    public const int BatchLength = 64 * 1024;

    public static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan s_retryInterval = TimeSpan.FromMilliseconds(500);

    private readonly PanelChannel _panel;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _subscriptions = new();
    private readonly Lock _lock = new();

    public ConsoleRelay(PanelChannel panel, TimeProvider clock)
    {
        _panel = panel;
        _clock = clock;
        panel.OnDisconnected(Clear);
    }

    /// <summary>Servers whose output is being sent (tests).</summary>
    public IReadOnlyCollection<Guid> Subscribed => [.. _subscriptions.Keys];

    /// <summary>Starts (or moves back) the subscription of <paramref name="serverId"/> to output after <paramref name="fromSequence"/>.</summary>
    public void Subscribe(Guid serverId, ConsoleBuffer buffer, long? fromSequence)
    {
        var subscription = new CancellationTokenSource();
        lock (_lock)
        {
            if (_subscriptions.TryRemove(serverId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            _subscriptions[serverId] = subscription;
        }

        var token = subscription.Token;
        _ = Task.Run(() => PumpAsync(serverId, buffer, fromSequence, token), CancellationToken.None);
    }

    /// <summary>False when the server was not subscribed.</summary>
    public bool Unsubscribe(Guid serverId)
    {
        lock (_lock)
        {
            if (!_subscriptions.TryRemove(serverId, out var subscription))
            {
                return false;
            }

            subscription.Cancel();
            subscription.Dispose();
            return true;
        }
    }

    /// <summary>Ends every subscription (the connection they belonged to is gone).</summary>
    public void Clear()
    {
        foreach (var serverId in _subscriptions.Keys)
        {
            Unsubscribe(serverId);
        }
    }

    public void Dispose() => Clear();

    private async Task PumpAsync(Guid serverId, ConsoleBuffer buffer, long? cursor, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await buffer.WaitAsync(cursor, cancellationToken);
                var entries = buffer.Read(cursor, BatchLength);
                if (entries.Count == 0)
                {
                    continue;
                }

                // Kept until sent: while the link is down the entries wait here (and the buffer keeps the rest).
                while (!await _panel.SendAsync(nameof(IAgentHub.ConsoleOutput), new ServerConsoleOutput(serverId, entries), cancellationToken))
                {
                    await Task.Delay(s_retryInterval, _clock, cancellationToken);
                }

                cursor = entries[^1].Sequence;
                await Task.Delay(SendInterval, _clock, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Unsubscribed, or the connection ended.
        }
    }
}
