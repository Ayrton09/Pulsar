using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Agents;

/// <summary>Marks a connection accepted on the agents' endpoint: its connection middleware sets it, requests see it.</summary>
internal sealed class AgentsEndpointFeature
{
    public static AgentsEndpointFeature Instance { get; } = new();
}

/// <summary><c>Agents</c> settings.</summary>
public sealed class AgentsOptions
{
    public const string SectionName = "Agents";

    /// <summary>
    /// The agents' address when it is not the host agents reach the panel by with the agents' endpoint's port (a
    /// port forwarded under another number, another name): an https origin such as https://agents.example.com:9444.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// The most connections the agents' endpoint holds at once (each agent keeps one for its hub, and opens one or two
    /// more for a while to sign in or transfer a file): about four per node. One more is closed before its TLS
    /// handshake. Kestrel's own limit counts every endpoint, the browsers' too.
    /// </summary>
    public int MaxConnections { get; set; } = 1000;

    /// <summary>
    /// The most of those that have not presented an agent session yet (handshakes, sign-ins, enrollments, and whatever
    /// never signs in): a quarter of <see cref="MaxConnections"/> unless set. An agent holds one for a moment at each
    /// sign-in and one until its hub's first request; the rest of the endpoint is left to signed-in agents.
    /// </summary>
    public int? MaxPendingConnections { get; set; }

    /// <summary>
    /// The most of those from one client address (an IPv4 address, an IPv6 /64): 20 unless set, and never more than
    /// <see cref="MaxPendingConnections"/>. Nodes behind one NAT share it, a sign-in at a time each.
    /// </summary>
    public int? MaxPendingConnectionsPerAddress { get; set; }

    internal int PendingLimit => MaxPendingConnections ?? Math.Max(1, MaxConnections / 4);

    internal int PendingPerAddressLimit => MaxPendingConnectionsPerAddress ?? Math.Min(20, PendingLimit);

    /// <summary>
    /// How long a connection has, from its arrival, to present an agent session before it is closed: an agent's sign-in
    /// takes a second or two, and its hub, transfers and rotation present their session in their first request. Not a
    /// setting (tests shorten it).
    /// </summary>
    internal TimeSpan SignInDeadline { get; set; } = TimeSpan.FromSeconds(30);

    internal static bool IsValidUrl(string? url) =>
        url is null || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0);
}

/// <summary>
/// The panel's agents' endpoint (Phase 16, docs/architecture/03 "Client certificates"): the Kestrel endpoint named
/// <see cref="Name"/> (<c>Kestrel:Endpoints:Agents</c>). HTTPS only, it asks every client for a certificate during the
/// handshake and serves only the agent protocol, so browsers never see a certificate request: they use the main
/// endpoint. Certificates are self-signed by each node on its node key; the panel matches the key with the node's
/// credential instead of checking a chain.
/// </summary>
internal sealed class AgentsEndpoint(IConfiguration configuration, IOptions<AgentsOptions> options, TimeProvider clock, ILogger<AgentsEndpoint> logger)
{
    public const string Name = "Agents";

    /// <summary>The TLS handshake's time on the endpoint, from the connection's arrival (an agent's takes well under a second).</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private int _port;

    /// <summary>The connection limit of the endpoint as Kestrel last set it up.</summary>
    internal AgentsConnectionLimit? ConnectionLimit { get; private set; }

    /// <summary>The endpoint is configured and Kestrel set it up (a configuration reload can remove it).</summary>
    public bool IsConfigured => Volatile.Read(ref _port) != 0 && configuration.GetSection($"Kestrel:Endpoints:{Name}").Exists();

    /// <summary>Kestrel (or a test) says the endpoint listens on <paramref name="port"/>.</summary>
    public void Listening(int port) => Volatile.Write(ref _port, port);

    /// <summary>
    /// What agents are told and what the panel signs: the port, and <c>Agents:PublicUrl</c> when set. Agents put the port
    /// on the host they reach the panel by; a request's Host header is never part of it. Null without an endpoint.
    /// </summary>
    public AgentsEndpointAddress? Address => IsConfigured
        ? new AgentsEndpointAddress(Volatile.Read(ref _port), options.Value.PublicUrl is { } url ? new Uri(url).GetLeftPart(UriPartial.Authority).ToLowerInvariant() : null)
        : null;

    /// <summary>
    /// The address to show an administrator in <paramref name="request"/>: <c>Agents:PublicUrl</c>, otherwise the host
    /// they reached the panel by with the endpoint's port. Display only: it is never signed. Null without an endpoint.
    /// </summary>
    public string? AddressFor(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Address is not { } address ? null : address.Url ?? $"https://{request.Host.Host}:{address.Port}".ToLowerInvariant();
    }

    /// <summary>How <paramref name="http"/> reached the panel.</summary>
    public AgentTransport TransportOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new AgentTransport(http.Features.Get<AgentsEndpointFeature>() is not null, ClientKey(http.Connection.ClientCertificate), Address);
    }

    /// <summary>Kestrel's configuration of the endpoint, when <c>Kestrel:Endpoints:Agents</c> exists.</summary>
    public void Configure(EndpointConfiguration endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsHttps)
        {
            throw new InvalidOperationException(
                $"Kestrel:Endpoints:{Name} must be an https endpoint: agents present their client certificate in the TLS handshake.");
        }

        if (endpoint.ListenOptions.IPEndPoint is not { Port: > 0 } address)
        {
            throw new InvalidOperationException($"Kestrel:Endpoints:{Name} needs a fixed port: agents are told its address.");
        }

        endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        endpoint.HttpsOptions.AllowAnyClientCertificate();
        endpoint.HttpsOptions.CheckCertificateRevocation = false;
        endpoint.HttpsOptions.OnAuthenticate = (_, ssl) => ssl.CertificateChainPolicy = ClientChainPolicy();
        // Half Kestrel's 10 s: a connection that sends nothing holds a place for less.
        endpoint.HttpsOptions.HandshakeTimeout = HandshakeTimeout;
        // Before TLS (Kestrel adds its HTTPS middleware after this configuration), which is where a connection costs.
        var limit = new AgentsConnectionLimit(options.Value, logger, clock);
        ConnectionLimit = limit;
        endpoint.ListenOptions.Use(limit.Wrap);
        endpoint.ListenOptions.Use(next => connection =>
        {
            connection.Features.Set(AgentsEndpointFeature.Instance);
            return next(connection);
        });
        Listening(address.Port);
    }

    /// <summary>
    /// How TLS builds the chain of a client's certificate before Kestrel accepts any: with what the handshake brought and
    /// the machine's stores only. The panel checks the key, never the chain, and the certificate is anyone's choice
    /// before that: the URLs it names (its issuer's certificate, revocation lists, OCSP) would make the panel connect
    /// wherever they say, and hold the handshake while it does. On Windows no downloads also means no automatic root
    /// update. The machine's trust decides nothing here (the result is ignored), and costs nothing more: a custom root
    /// trust would build a chain engine for every handshake, many times the work of the chain itself.
    /// </summary>
    internal static X509ChainPolicy ClientChainPolicy() => new()
    {
        RevocationMode = X509RevocationMode.NoCheck,
        DisableCertificateDownloads = true,
    };

    /// <summary>The certificate's key as SubjectPublicKeyInfo, re-encoded from the key; null without one or for another algorithm.</summary>
    private static byte[]? ClientKey(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        try
        {
            using var key = certificate.GetECDsaPublicKey();
            return key?.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// The connections the agents' endpoint holds at once (docs/architecture/08, T31), counted before anything is read from
/// them: at most <c>Agents:MaxConnections</c>, and of those that have not presented an agent session yet at most
/// <c>Agents:MaxPendingConnections</c>, <c>Agents:MaxPendingConnectionsPerAddress</c> from one address. One more is closed
/// at once. One not signed in by <see cref="AgentsOptions.SignInDeadline"/> is asked to close, as Kestrel closes an idle
/// keep-alive connection (after the request in progress), and aborted <see cref="CloseGrace"/> later if it is still open
/// and not signed in. Refusals and timeouts are logged at most once a minute, with how many there were.
/// </summary>
internal sealed partial class AgentsConnectionLimit(AgentsOptions options, ILogger logger, TimeProvider clock)
{
    /// <summary>What a connection asked to close at its deadline has to finish the request in progress.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan s_logInterval = TimeSpan.FromMinutes(1);

    private readonly int _max = options.MaxConnections;
    private readonly int _maxPending = options.PendingLimit;
    private readonly int _maxPerAddress = options.PendingPerAddressLimit;
    private readonly TimeSpan _deadline = options.SignInDeadline;
    private readonly Lock _lock = new();
    private readonly Dictionary<ClientAddress, int> _pendingByAddress = [];
    private int _open;
    private int _pending;
    private int _refusedFull;
    private int _refusedPending;
    private int _refusedAddress;
    private int _timedOut;
    private long _nextLog = long.MinValue;

    /// <summary>The connections held now.</summary>
    public int Open
    {
        get
        {
            lock (_lock)
            {
                return _open;
            }
        }
    }

    /// <summary>Of those, the ones not signed in yet.</summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    /// <summary>The addresses those come from.</summary>
    public int Addresses
    {
        get
        {
            lock (_lock)
            {
                return _pendingByAddress.Count;
            }
        }
    }

    public ConnectionDelegate Wrap(ConnectionDelegate next) => async connection =>
    {
        var address = ClientAddress.Of(connection.RemoteEndPoint);
        if (!TryOpen(address))
        {
            return;
        }

        using var counted = new AgentsEndpointConnection(this, connection, address);
        counted.StartDeadline(clock, _deadline);
        connection.Features.Set(counted);
        await next(connection);
    };

    private bool TryOpen(ClientAddress? address)
    {
        lock (_lock)
        {
            var fromAddress = address is { } key ? _pendingByAddress.GetValueOrDefault(key) : 0;
            if (_open >= _max)
            {
                _refusedFull++;
            }
            else if (_pending >= _maxPending)
            {
                _refusedPending++;
            }
            else if (fromAddress >= _maxPerAddress)
            {
                _refusedAddress++;
            }
            else
            {
                _open++;
                _pending++;
                if (address is { } added)
                {
                    _pendingByAddress[added] = fromAddress + 1;
                }

                return true;
            }
        }

        Report();
        return false;
    }

    /// <summary>A connection presented an agent session: it leaves the budgets of those not signed in.</summary>
    internal void SignedIn(ClientAddress? address)
    {
        lock (_lock)
        {
            LeavePending(address);
        }
    }

    /// <summary>A connection ended, <paramref name="pending"/> when it never signed in.</summary>
    internal void Closed(ClientAddress? address, bool pending)
    {
        lock (_lock)
        {
            _open--;
            if (pending)
            {
                LeavePending(address);
            }
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

    private void LeavePending(ClientAddress? address)
    {
        _pending--;
        if (address is { } key)
        {
            var count = _pendingByAddress[key] - 1;
            if (count == 0)
            {
                _pendingByAddress.Remove(key);
            }
            else
            {
                _pendingByAddress[key] = count;
            }
        }
    }

    private void Report()
    {
        var now = clock.GetUtcNow().UtcTicks;
        var next = Interlocked.Read(ref _nextLog);
        if (now >= next && Interlocked.CompareExchange(ref _nextLog, now + s_logInterval.Ticks, next) == next)
        {
            int full, pending, address, timedOut;
            lock (_lock)
            {
                (full, pending, address, timedOut) = (_refusedFull, _refusedPending, _refusedAddress, _timedOut);
                _refusedFull = _refusedPending = _refusedAddress = _timedOut = 0;
            }

            LogClosed(full + pending + address, full, pending, address, timedOut, (int)_deadline.TotalSeconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The agents' endpoint closed {Refused} connection(s) on arrival ({Full} over Agents:MaxConnections, "
            + "{OverPending} over Agents:MaxPendingConnections, {OverAddress} over Agents:MaxPendingConnectionsPerAddress) "
            + "and {TimedOut} that presented no agent session within {Deadline} s")]
    private partial void LogClosed(int refused, int full, int overPending, int overAddress, int timedOut, int deadline);
}

/// <summary>
/// Where connections not signed in are counted together: an IPv4 address (also when mapped into IPv6), or the /64 of an
/// IPv6 address, the least one host or one home network has.
/// </summary>
internal readonly record struct ClientAddress(bool IPv6, ulong Bits)
{
    public static ClientAddress? Of(EndPoint? endpoint)
    {
        if (endpoint is not IPEndPoint { Address: var address })
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            return null;
        }

        return written == 4
            ? new ClientAddress(false, BinaryPrimitives.ReadUInt32BigEndian(bytes))
            : new ClientAddress(true, BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }
}

/// <summary>
/// A connection the agents' endpoint counts, a connection feature its requests see: not signed in until a request on it
/// presents a valid agent session (<see cref="AgentAuthenticationHandler"/>), then never timed out. Challenges, tokens
/// and enrollments do not sign a connection in: they are what a stranger can send.
/// </summary>
internal sealed class AgentsEndpointConnection(AgentsConnectionLimit limit, ConnectionContext connection, ClientAddress? address) : IDisposable
{
    private readonly Lock _lock = new();
    private ITimer? _deadline;
    private State _state;
    private bool _askedToClose;

    private enum State
    {
        Pending,
        SignedIn,
        Closed,
    }

    /// <summary>A request on the connection presented a valid agent session.</summary>
    public void Authenticated()
    {
        lock (_lock)
        {
            if (_state != State.Pending)
            {
                return;
            }

            _state = State.SignedIn;
            _deadline?.Dispose();
        }

        limit.SignedIn(address);
    }

    internal void StartDeadline(TimeProvider clock, TimeSpan deadline)
    {
        lock (_lock)
        {
            _deadline = clock.CreateTimer(static state => ((AgentsEndpointConnection)state!).Expire(), this, deadline, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The connection ended.</summary>
    public void Dispose()
    {
        bool pending;
        lock (_lock)
        {
            if (_state == State.Closed)
            {
                return;
            }

            pending = _state == State.Pending;
            _state = State.Closed;
            _deadline?.Dispose();
        }

        limit.Closed(address, pending);
    }

    private void Expire()
    {
        bool abort;
        lock (_lock)
        {
            if (_state != State.Pending)
            {
                return;
            }

            abort = _askedToClose;
            if (!abort)
            {
                _askedToClose = true;
                _deadline!.Change(AgentsConnectionLimit.CloseGrace, Timeout.InfiniteTimeSpan);
            }
        }

        if (abort)
        {
            connection.Abort(new ConnectionAbortedException("No agent session within the endpoint's deadline."));
            return;
        }

        limit.TimedOut();
        connection.Features.Get<IConnectionLifetimeNotificationFeature>()?.RequestClose();
    }
}

internal static class AgentsEndpointExtensions
{
    /// <summary>The agents' endpoint answers only the agent protocol: the rest of the panel is 404 there.</summary>
    public static IApplicationBuilder UseAgentsEndpoint(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        if (context.Features.Get<AgentsEndpointFeature>() is not null && !context.Request.Path.StartsWithSegments(AgentProtocol.BasePath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    });
}
