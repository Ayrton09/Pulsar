using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
    /// sign-in and one until its hub's first request; the rest of the endpoint is left to signed-in agents. Half of it
    /// is kept for addresses an agent signed in from within a day.
    /// </summary>
    public int? MaxPendingConnections { get; set; }

    /// <summary>
    /// The most of those from one client address (an IPv4 address, an IPv6 /64, the panel's own machine): 20 unless
    /// set, and never more than <see cref="MaxPendingConnections"/>. Nodes behind one NAT share it, a sign-in at a time each.
    /// </summary>
    public int? MaxPendingConnectionsPerAddress { get; set; }

    /// <summary>
    /// The most of those from one network (an IPv4 /24, an IPv6 /48): three times
    /// <see cref="MaxPendingConnectionsPerAddress"/> unless set, between that and <see cref="MaxPendingConnections"/>.
    /// A host with several addresses or a routed IPv6 prefix gets this much, not a budget per address.
    /// </summary>
    public int? MaxPendingConnectionsPerNetwork { get; set; }

    /// <summary>
    /// The most connections one node's sessions hold at once: 16 unless set (its hub, a rotation and its file transfers
    /// at the same time), and never more than <see cref="MaxConnections"/>. A request over it is refused.
    /// </summary>
    public int? MaxConnectionsPerNode { get; set; }

    internal int PendingLimit => MaxPendingConnections ?? Math.Max(1, MaxConnections / 4);

    internal int PendingPerAddressLimit => MaxPendingConnectionsPerAddress ?? Math.Min(20, PendingLimit);

    internal int PendingPerNetworkLimit => MaxPendingConnectionsPerNetwork ?? (int)Math.Min(3L * PendingPerAddressLimit, PendingLimit);

    internal int PerNodeLimit => MaxConnectionsPerNode ?? Math.Min(16, MaxConnections);

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
    private AgentsConnectionLimit? _limit;

    /// <summary>The endpoint's connections: one count for every configuration Kestrel loads, so a node's can be closed.</summary>
    internal AgentsConnectionLimit ConnectionLimit =>
        LazyInitializer.EnsureInitialized(ref _limit, () => new AgentsConnectionLimit(options.Value, logger, clock));

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
        // TCP only, whatever the configuration says: HTTP/3 (QUIC) would reach the panel without the budgets below.
        endpoint.ListenOptions.Protocols = HttpProtocols.Http1AndHttp2;
        // Before TLS (Kestrel adds its HTTPS middleware after this configuration), which is where a connection costs.
        endpoint.ListenOptions.Use(ConnectionLimit.Wrap);
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
