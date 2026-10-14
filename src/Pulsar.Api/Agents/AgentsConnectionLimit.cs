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
/// take half at most; <c>Agents:MaxPendingConnectionsPerAddress</c> from one <see cref="ClientAddress"/>, and from
/// addresses not known <c>Agents:MaxPendingConnectionsPerNetwork</c> per network (a known agent's neighbours never take
/// its address's places). One more is closed at once, before its TLS handshake. Of those signed in, at most
/// <c>Agents:MaxConnectionsPerNode</c> for one node, of which its transfers take all but a reserve for its hub, the hub
/// it replaces and its rotation. A node's sign-ins make its latest <see cref="KnownPerNode"/> addresses known, and past
/// the bound of the whole set the node with the most loses its oldest: one key can neither open the reserved half to
/// many addresses nor push the others' out. Refusals and timeouts are logged at most once a minute, with how many there
/// were and the nodes refused.
/// </summary>
internal sealed partial class AgentsConnectionLimit(AgentsOptions options, ILogger logger, TimeProvider clock)
{
    /// <summary>What a connection asked to close has to finish the request in progress.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long an address an agent signed in from keeps its place in the reserved half of the pending budget.</summary>
    public static readonly TimeSpan KnownFor = TimeSpan.FromDays(1);

    /// <summary>How many addresses one node's sign-ins make known: its latest ones (an IPv4 and an IPv6 address, a move).</summary>
    public const int KnownPerNode = 4;

    private const int LoggedNodes = 8;

    private static readonly TimeSpan s_logInterval = TimeSpan.FromMinutes(1);

    private readonly int _max = options.MaxConnections;
    private readonly int _maxPending = options.PendingLimit;
    private readonly int _maxUnknown = options.PendingLimit - (options.PendingLimit / 2);
    private readonly int _maxPerAddress = options.PendingPerAddressLimit;
    private readonly int _maxPerNetwork = options.PendingPerNetworkLimit;
    private readonly int _maxPerNode = options.PerNodeLimit;
    private readonly int _maxTransfersPerNode = options.PerNodeLimit - Math.Min(4, options.PerNodeLimit / 2);
    private readonly int _maxKnown = Math.Max(4096, options.MaxConnections);
    private readonly TimeSpan _deadline = options.SignInDeadline;
    private readonly Lock _lock = new();
    private readonly Dictionary<ClientAddress, int> _pendingByAddress = [];
    private readonly Dictionary<ClientAddress, int> _pendingByNetwork = [];
    private readonly Dictionary<NodeId, HashSet<AgentsEndpointConnection>> _byNode = [];
    private readonly Dictionary<NodeId, int> _transfersByNode = [];
    private readonly Dictionary<ClientAddress, KnownAddress> _known = [];
    private readonly Dictionary<NodeId, List<(ClientAddress Address, DateTimeOffset Until)>> _knownByNode = [];
    private readonly HashSet<NodeId> _refusedNodes = [];
    private int _knownEntries;
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
    /// <paramref name="connection"/> carries a session of <paramref name="node"/> now (for a <paramref name="transfer"/>):
    /// it leaves the budgets of those not signed in, and its address is known. False when the node holds as many
    /// connections as it may, or as many for transfers.
    /// </summary>
    internal bool SignIn(AgentsEndpointConnection connection, NodeId node, bool transfer)
    {
        lock (_lock)
        {
            _byNode.TryGetValue(node, out var connections);
            var transfers = _transfersByNode.GetValueOrDefault(node);
            if ((connections?.Count ?? 0) < _maxPerNode && (!transfer || transfers < _maxTransfersPerNode))
            {
                if (connections is null)
                {
                    _byNode[node] = connections = [];
                }

                connections.Add(connection);
                if (transfer)
                {
                    _transfersByNode[node] = transfers + 1;
                }

                LeavePending(connection);
                Remember(connection.Address, node);
                return true;
            }

            _refusedNode++;
            if (_refusedNodes.Count < LoggedNodes)
            {
                _refusedNodes.Add(node);
            }
        }

        Report();
        return false;
    }

    /// <summary>
    /// A connection ended: <paramref name="pending"/> when it never signed in, otherwise one of <paramref name="node"/>
    /// (for a <paramref name="transfer"/>).
    /// </summary>
    internal void Closed(AgentsEndpointConnection connection, bool pending, NodeId? node, bool transfer)
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

                if (transfer)
                {
                    Decrement(_transfersByNode, id);
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
            var fromNetwork = unknown && address is { } n ? _pendingByNetwork.GetValueOrDefault(n.Network) : 0;
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
            else if (unknown && fromNetwork >= _maxPerNetwork)
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
                    if (unknown)
                    {
                        _pendingByNetwork[added.Network] = fromNetwork + 1;
                    }
                }

                return unknown;
            }
        }

        Report();
        return null;
    }

    private bool IsKnown(ClientAddress address) => _known.TryGetValue(address, out var known) && known.Until > clock.GetUtcNow();

    /// <summary>
    /// Keeps <paramref name="address"/> as one of <paramref name="node"/>'s, its latest <see cref="KnownPerNode"/> only;
    /// past the bound of the whole set, the node with the most addresses loses its oldest.
    /// </summary>
    private void Remember(ClientAddress? address, NodeId node)
    {
        if (address is not { } key)
        {
            return;
        }

        var until = clock.GetUtcNow() + KnownFor;
        if (!_knownByNode.TryGetValue(node, out var entries))
        {
            _knownByNode[node] = entries = [];
        }

        var index = entries.FindIndex(entry => entry.Address == key);
        if (index >= 0)
        {
            entries.RemoveAt(index);
        }
        else
        {
            _knownEntries++;
            if (_known.TryGetValue(key, out var held))
            {
                held.Holders++;
            }
            else
            {
                _known[key] = new KnownAddress();
            }
        }

        entries.Add((key, until));
        var known = _known[key];
        known.Until = until > known.Until ? until : known.Until;
        if (entries.Count > KnownPerNode)
        {
            ForgetOldest(node, entries);
        }

        while (_knownEntries > _maxKnown)
        {
            var fullest = _knownByNode.MaxBy(holder => holder.Value.Count);
            ForgetOldest(fullest.Key, fullest.Value);
        }
    }

    private void ForgetOldest(NodeId node, List<(ClientAddress Address, DateTimeOffset Until)> entries)
    {
        var address = entries[0].Address;
        entries.RemoveAt(0);
        _knownEntries--;
        if (entries.Count == 0)
        {
            _knownByNode.Remove(node);
        }

        if (--_known[address].Holders == 0)
        {
            _known.Remove(address);
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
            if (connection.Unknown)
            {
                Decrement(_pendingByNetwork, address.Network);
            }
        }
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
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
            string nodes;
            lock (_lock)
            {
                (full, pending, address, network, node, timedOut) = (_refusedFull, _refusedPending, _refusedAddress, _refusedNetwork, _refusedNode, _timedOut);
                _refusedFull = _refusedPending = _refusedAddress = _refusedNetwork = _refusedNode = _timedOut = 0;
                nodes = _refusedNodes.Count == 0 ? "none" : string.Join(", ", _refusedNodes.Select(n => n.Value));
                _refusedNodes.Clear();
            }

            LogClosed(full + pending + address + network, full, pending, address, network, node, nodes, timedOut);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The agents' endpoint closed {Refused} connection(s) on arrival ({Full} over Agents:MaxConnections, "
            + "{OverPending} over Agents:MaxPendingConnections or the half of it for addresses no agent signed in from lately, "
            + "{OverAddress} over Agents:MaxPendingConnectionsPerAddress, {OverNetwork} over Agents:MaxPendingConnectionsPerNetwork), "
            + "refused {OverNode} request(s) over Agents:MaxConnectionsPerNode or the part of it for transfers (nodes: {Nodes}), "
            + "and closed {TimedOut} that presented no agent session in time")]
    private partial void LogClosed(int refused, int full, int overPending, int overAddress, int overNetwork, int overNode, string nodes, int timedOut);

    /// <summary>An address agents signed in from: how many nodes keep it, and until when.</summary>
    private sealed class KnownAddress
    {
        public int Holders { get; set; } = 1;

        public DateTimeOffset Until { get; set; }
    }
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
    private bool _transfer;
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
    public IDisposable? Authenticated(NodeId node, Guid sessionId, DateTimeOffset sessionEnds, bool transfer = false)
    {
        lock (_lock)
        {
            if (_state == State.Pending)
            {
                if (!limit.SignIn(this, node, transfer))
                {
                    return null;
                }

                _state = State.SignedIn;
                _node = node;
                _transfer = transfer;
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
            limit.Closed(this, pending, node, _transfer);
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
