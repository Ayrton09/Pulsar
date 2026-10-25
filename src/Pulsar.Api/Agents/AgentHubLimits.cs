using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.AspNetCore.SignalR;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Agents;

/// <summary>
/// How often one agent connection may call each hub method (docs/architecture/08, T32): whoever holds a node's key or a
/// session token cannot load the panel's database, its log and every watching browser by calling in a loop. The rates are
/// far above what an agent sends (console output every 50 ms per watched server, metrics every few seconds, progress,
/// results replayed after a reconnect); a call past them is refused (a delivery the agent retries later) and the node is
/// logged once a minute while it goes on. Hello is said once per connection: each one writes the node's row and reconciles
/// all its servers.
/// </summary>
internal sealed partial class AgentHubLimits(TimeProvider clock, ILogger<AgentHubLimits> logger) : IHubFilter
{
    /// <summary>Calls a second and the burst allowed, per method; methods not listed have no limit of their own.</summary>
    internal static readonly FrozenDictionary<string, (double PerSecond, double Burst)> Rates =
        new Dictionary<string, (double PerSecond, double Burst)>(StringComparer.Ordinal)
        {
            [nameof(IAgentHub.ConsoleOutput)] = (500, 2000),
            [nameof(IAgentHub.Metrics)] = (10, 30),
            [nameof(IAgentHub.NodeMetrics)] = (10, 30),
            [nameof(IAgentHub.OperationProgress)] = (100, 500),
            [nameof(IAgentHub.OperationCompleted)] = (50, 500),
            [nameof(IAgentHub.ServerStateChanged)] = (50, 500),
            [nameof(IAgentHub.TransferCompleted)] = (50, 200),
            [nameof(IAgentHub.UpdateReport)] = (0.2, 10),
            [nameof(IAgentHub.GameUpdateChecked)] = (5, 50),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly TimeSpan s_logInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, ConnectionLimits> _connections = new(StringComparer.Ordinal);

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);
        var context = invocationContext.Context;
        var limits = _connections.GetOrAdd(context.ConnectionId, _ => new ConnectionLimits());
        if (limits.Refuse(invocationContext.HubMethodName, clock, out var logNow) is { } refusal)
        {
            if (logNow)
            {
                LogRefused(context.User?.NodeId(), invocationContext.HubMethodName);
            }

            throw new HubException(refusal);
        }

        return await next(invocationContext);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        _connections.TryRemove(context.Context.ConnectionId, out _);
        await next(context, exception);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} calls {Method} faster than any agent does: the calls past the limit are refused")]
    private partial void LogRefused(NodeId? nodeId, string method);

    private sealed class ConnectionLimits
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, (double Tokens, long At)> _buckets = new(StringComparer.Ordinal);
        private bool _helloSaid;
        private long? _loggedAt;

        /// <summary>Why the call is refused, or null; <paramref name="logNow"/> when a refusal is to be logged.</summary>
        public string? Refuse(string method, TimeProvider clock, out bool logNow)
        {
            logNow = false;
            lock (_lock)
            {
                if (method == nameof(IAgentHub.Hello))
                {
                    if (!_helloSaid)
                    {
                        _helloSaid = true;
                        return null;
                    }
                }
                else if (!Rates.TryGetValue(method, out var rate) || Take(method, rate, clock.GetTimestamp(), clock))
                {
                    return null;
                }

                var now = clock.GetTimestamp();
                if (_loggedAt is not { } at || clock.GetElapsedTime(at, now) >= s_logInterval)
                {
                    _loggedAt = now;
                    logNow = true;
                }

                return method == nameof(IAgentHub.Hello)
                    ? "Hello was said already on this connection."
                    : $"Too many {method} calls on this connection; try again later.";
            }
        }

        private bool Take(string method, (double PerSecond, double Burst) rate, long now, TimeProvider clock)
        {
            var (tokens, at) = _buckets.TryGetValue(method, out var bucket) ? bucket : (rate.Burst, now);
            tokens = Math.Min(rate.Burst, tokens + (clock.GetElapsedTime(at, now).TotalSeconds * rate.PerSecond));
            var taken = tokens >= 1;
            _buckets[method] = (taken ? tokens - 1 : tokens, now);
            return taken;
        }
    }
}
