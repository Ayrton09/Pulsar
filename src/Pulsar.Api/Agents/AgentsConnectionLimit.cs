using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Pulsar.Api.Http;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Agents;

/// <summary>
/// The connections the agents' endpoint holds at once (docs/architecture/03 "Client certificates", doc 08 T31), counted
/// before anything is read from them. At most <c>Agents:MaxConnections</c> in all. Of those not signed in yet (no request
/// on them presented a valid agent session with the key of their certificate, <see cref="AgentsEndpointConnection"/>):
/// at most <c>Agents:MaxPendingConnections</c>, of which addresses no agent signed in from within <see cref="KnownFor"/>
/// take half at most; <c>Agents:MaxPendingConnectionsPerAddress</c> from one <see cref="ClientAddress"/> and
/// <c>Agents:MaxPendingConnectionsPerNetwork</c> from its network. One more is closed at once, before its TLS handshake.
/// Of those signed in, at most <c>Agents:MaxConnectionsPerNode</c> for one node. Refusals and timeouts are logged at most
/// once a minute, with how many there were.
/// </summary>
internal sealed partial class AgentsConnectionLimit(AgentsOptions options, ILogger logger, TimeProvider clock)
{
    /// <summary>What a connection asked to close has to finish the request in progress.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long an address an agent signed in from keeps its place in the reserved half of the pending budget.</summary>
    public static readonly TimeSpan KnownFor = TimeSpan.FromDays(1);

    private static readonly TimeSpan s_logInterval = TimeSpan.FromMinutes(1);

    private readonly int _max = options.MaxConnections;
    private readonly int _maxPending = options.PendingLimit;
    private readonly int _maxUnknown = options.PendingLimit - (options.PendingLimit / 2);
    private readonly int _maxPerAddress = options.PendingPerAddressLimit;
    private readonly int _maxPerNetwork = options.PendingPerNetworkLimit;
    private readonly int _maxPerNode = options.PerNodeLimit;
    private readonly int _maxKnown = Math.Max(4096, options.MaxConnections);
    private readonly TimeSpan _deadline = options.SignInDeadline;
    private readonly Lock _lock = new();
    private readonly Dictionary<ClientAddress, int> _pendingByAddress = [];
    private readonly Dictionary<ClientAddress, int> _pendingByNetwork = [];
    private readonly Dictionary<NodeId, HashSet<AgentsEndpointConnection>> _byNode = [];
    private readonly Dictionary<ClientAddress, DateTimeOffset> _known = [];
    private int _open;
    private int _pending;
    private int _pendingUnknown;
    private int _refusedFull;
    private int _refusedPending;
    private int _refusedAddress;
    private int _refusedNetwork;
    private int _refusedNode;
    private int _timedOut;
    private long _nextLog = long.MinValue;

    /// <summary>The connections held now.</summary>
    public int Open => Read(() => _open);

    /// <summary>Of those, the ones not signed in yet.</summary>
    public int Pending => Read(() => _pending);

    /// <summary>The addresses those come from.</summary>
    public int Addresses => Read(() => _pendingByAddress.Count);

    /// <summary>The networks those come from.</summary>
    public int Networks => Read(() => _pendingByNetwork.Count);

    /// <summary>The connections signed in for <paramref name="node"/>.</summary>
    public int SignedIn(NodeId node) => Read(() => _byNode.TryGetValue(node, out var connections) ? connections.Count : 0);

    /// <summary>The addresses agents signed in from lately.</summary>
    public int Known => Read(() => _known.Count);

    public ConnectionDelegate Wrap(ConnectionDelegate next) => async connection =>
    {
        var address = ClientAddress.Of(connection.RemoteEndPoint);
        if (TryOpen(address) is not { } unknown)
        {
            return;
        }

        using var counted = new AgentsEndpointConnection(this, connection, address, unknown);
        counted.Start(clock, _deadline);
        connection.Features.Set(counted);
        await next(connection);
    };

    /// <summary>
    /// <paramref name="connection"/> carries a session of <paramref name="node"/> now: it leaves the budgets of those not
    /// signed in, and its address is known. False when the node holds as many connections as it may.
    /// </summary>
    internal bool SignIn(AgentsEndpointConnection connection, NodeId node)
    {
        lock (_lock)
        {
            if (!_byNode.TryGetValue(node, out var connections) || connections.Count < _maxPerNode)
            {
                if (connections is null)
                {
                    _byNode[node] = connections = [];
                }

                connections.Add(connection);
                LeavePending(connection);
                Remember(connection.Address);
                return true;
            }

            _refusedNode++;
        }

        Report();
        return false;
    }

    /// <summary>A connection ended: <paramref name="pending"/> when it never signed in, otherwise one of <paramref name="node"/>.</summary>
    internal void Closed(AgentsEndpointConnection connection, bool pending, NodeId? node)
    {
        lock (_lock)
        {
            _open--;
            if (pending)
            {
                LeavePending(connection);
            }
            else if (node is { } id && _byNode.TryGetValue(id, out var connections))
            {
                connections.Remove(connection);
                if (connections.Count == 0)
                {
                    _byNode.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Closes, now, the connections of <paramref name="node"/> whose session ended (revocation, disabling, deletion, a key
    /// superseded by a rotation): they would otherwise stay until the session's end.
    /// </summary>
    public void CloseEnded(NodeId node, Func<Guid, bool> isActive)
    {
        ArgumentNullException.ThrowIfNull(isActive);
        AgentsEndpointConnection[] connections;
        lock (_lock)
        {
            connections = _byNode.TryGetValue(node, out var held) ? [.. held] : [];
        }

        foreach (var connection in connections)
        {
            connection.CloseIfEnded(isActive);
        }
    }

    internal void TimedOut()
    {
        lock (_lock)
        {
            _timedOut++;
        }

        Report();
    }

    private bool? TryOpen(ClientAddress? address)
    {
        lock (_lock)
        {
            var unknown = address is not { } seen || !IsKnown(seen);
            var fromAddress = address is { } a ? _pendingByAddress.GetValueOrDefault(a) : 0;
            var fromNetwork = address is { } n ? _pendingByNetwork.GetValueOrDefault(n.Network) : 0;
            if (_open >= _max)
            {
                _refusedFull++;
            }
            else if (_pending >= _maxPending || (unknown && _pendingUnknown >= _maxUnknown))
            {
                _refusedPending++;
            }
            else if (fromAddress >= _maxPerAddress)
            {
                _refusedAddress++;
            }
            else if (fromNetwork >= _maxPerNetwork)
            {
                _refusedNetwork++;
            }
            else
            {
                _open++;
                _pending++;
                if (unknown)
                {
                    _pendingUnknown++;
                }

                if (address is { } added)
                {
                    _pendingByAddress[added] = fromAddress + 1;
                    _pendingByNetwork[added.Network] = fromNetwork + 1;
                }

                return unknown;
            }
        }

        Report();
        return null;
    }

    private bool IsKnown(ClientAddress address)
    {
        if (!_known.TryGetValue(address, out var until))
        {
            return false;
        }

        if (until > clock.GetUtcNow())
        {
            return true;
        }

        _known.Remove(address);
        return false;
    }

    /// <summary>Keeps <paramref name="address"/> as an agent's; past the bound, expired entries go, then the oldest.</summary>
    private void Remember(ClientAddress? address)
    {
        if (address is not { } key)
        {
            return;
        }

        var now = clock.GetUtcNow();
        _known[key] = now + KnownFor;
        if (_known.Count <= _maxKnown)
        {
            return;
        }

        foreach (var (stale, until) in _known)
        {
            if (until <= now)
            {
                _known.Remove(stale);
            }
        }

        if (_known.Count > _maxKnown)
        {
            _known.Remove(_known.MinBy(entry => entry.Value).Key);
        }
    }

    private void LeavePending(AgentsEndpointConnection connection)
    {
        _pending--;
        if (connection.Unknown)
        {
            _pendingUnknown--;
        }

        if (connection.Address is { } address)
        {
            Decrement(_pendingByAddress, address);
            Decrement(_pendingByNetwork, address.Network);
        }
    }

    private static void Decrement(Dictionary<ClientAddress, int> counts, ClientAddress key)
    {
        var count = counts[key] - 1;
        if (count == 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = count;
        }
    }

    private T Read<T>(Func<T> read)
    {
        lock (_lock)
        {
            return read();
        }
    }

    private void Report()
    {
        var now = clock.GetUtcNow().UtcTicks;
        var next = Interlocked.Read(ref _nextLog);
        if (now >= next && Interlocked.CompareExchange(ref _nextLog, now + s_logInterval.Ticks, next) == next)
        {
            int full, pending, address, network, node, timedOut;
            lock (_lock)
            {
                (full, pending, address, network, node, timedOut) = (_refusedFull, _refusedPending, _refusedAddress, _refusedNetwork, _refusedNode, _timedOut);
                _refusedFull = _refusedPending = _refusedAddress = _refusedNetwork = _refusedNode = _timedOut = 0;
            }

            LogClosed(full + pending + address + network, full, pending, address, network, node, timedOut);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The agents' endpoint closed {Refused} connection(s) on arrival ({Full} over Agents:MaxConnections, "
            + "{OverPending} over Agents:MaxPendingConnections or the half of it for addresses no agent signed in from lately, "
            + "{OverAddress} over Agents:MaxPendingConnectionsPerAddress, {OverNetwork} over Agents:MaxPendingConnectionsPerNetwork), "
            + "refused {OverNode} request(s) over Agents:MaxConnectionsPerNode, and closed {TimedOut} that presented no agent session in time")]
    private partial void LogClosed(int refused, int full, int overPending, int overAddress, int overNetwork, int overNode, int timedOut);
}

/// <summary>
/// A connection the agents' endpoint counts, a connection feature its requests see. Not signed in until a request on it
/// presents a valid agent session with the key of its certificate (<see cref="AgentAuthenticationHandler"/>):
/// challenges, tokens and enrollments do not sign a connection in, since anyone can send them. One not signed in by the
/// deadline is asked to close, as Kestrel closes an idle keep-alive connection (after the request in progress), and
/// aborted <see cref="AgentsConnectionLimit.CloseGrace"/> later if it still is not. One signed in belongs to its node and
/// lasts as long as the latest session it presented: then it is asked to close in the same way, and aborted unless a
/// request of a session is still in progress (a transfer is never cut). Requests without a session do not keep it.
/// </summary>
internal sealed class AgentsEndpointConnection(AgentsConnectionLimit limit, ConnectionContext connection, ClientAddress? address, bool unknown) : IDisposable
{
    private readonly Lock _lock = new();
    private TimeProvider _clock = TimeProvider.System;
    private ITimer? _timer;
    private State _state;
    private bool _askedToClose;
    private NodeId _node;
    private Guid _session;
    private DateTimeOffset _sessionEnds;
    private int _requests;

    private enum State
    {
        Pending,
        SignedIn,
        Closed,
    }

    public ClientAddress? Address => address;

    /// <summary>It came from an address no agent signed in from lately.</summary>
    public bool Unknown => unknown;

    /// <summary>
    /// A request on the connection presented a valid session of <paramref name="node"/>: the connection is signed in, until
    /// the latest session it presented ends. Returns what the request disposes when it ends, or null when the node holds as
    /// many connections as it may or the connection belongs to another node: the request is then refused.
    /// </summary>
    public IDisposable? Authenticated(NodeId node, Guid sessionId, DateTimeOffset sessionEnds)
    {
        lock (_lock)
        {
            if (_state == State.Pending)
            {
                if (!limit.SignIn(this, node))
                {
                    return null;
                }

                _state = State.SignedIn;
                _node = node;
            }
            else if (_state != State.SignedIn || _node != node)
            {
                return null;
            }

            if (sessionEnds > _sessionEnds)
            {
                _session = sessionId;
                _sessionEnds = sessionEnds;
                _timer?.Change(Until(sessionEnds), Timeout.InfiniteTimeSpan);
            }

            _requests++;
            return new Request(this);
        }
    }

    internal void Start(TimeProvider clock, TimeSpan deadline)
    {
        lock (_lock)
        {
            _clock = clock;
            _timer = clock.CreateTimer(static state => ((AgentsEndpointConnection)state!).Expire(), this, deadline, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Aborts the connection when the session it carries ended.</summary>
    internal void CloseIfEnded(Func<Guid, bool> isActive)
    {
        lock (_lock)
        {
            if (_state != State.SignedIn || isActive(_session))
            {
                return;
            }
        }

        connection.Abort(new ConnectionAbortedException("The agent session of this connection ended."));
    }

    /// <summary>The connection ended.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_state == State.Closed)
            {
                return;
            }

            var pending = _state == State.Pending;
            NodeId? node = _state == State.SignedIn ? _node : null;
            _state = State.Closed;
            _timer?.Dispose();
            limit.Closed(this, pending, node);
        }
    }

    private TimeSpan Until(DateTimeOffset moment)
    {
        var left = moment - _clock.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void Expire()
    {
        bool abort;
        bool pending;
        lock (_lock)
        {
            if (_state == State.Closed)
            {
                return;
            }

            if (_state == State.SignedIn && Until(_sessionEnds) > TimeSpan.Zero)
            {
                _timer!.Change(Until(_sessionEnds), Timeout.InfiniteTimeSpan);
                return;
            }

            pending = _state == State.Pending;
            abort = _askedToClose;
            if (abort && !pending && _requests > 0)
            {
                // A request of a session, a transfer, is never cut: the connection closes once it is answered.
                _timer!.Change(AgentsConnectionLimit.CloseGrace, Timeout.InfiniteTimeSpan);
                return;
            }

            if (!abort)
            {
                _askedToClose = true;
                _timer!.Change(AgentsConnectionLimit.CloseGrace, Timeout.InfiniteTimeSpan);
            }
        }

        if (abort)
        {
            connection.Abort(new ConnectionAbortedException("No valid agent session on this connection in time."));
            return;
        }

        // Signed-in connections reach this at the end of their session, which is routine: only the others are counted.
        if (pending)
        {
            limit.TimedOut();
        }

        connection.Features.Get<IConnectionLifetimeNotificationFeature>()?.RequestClose();
    }

    private void RequestEnded()
    {
        lock (_lock)
        {
            _requests--;
        }
    }

    private sealed class Request(AgentsEndpointConnection owner) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                owner.RequestEnded();
            }
        }
    }
}
