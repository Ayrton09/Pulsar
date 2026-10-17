using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using Pulsar.Api.Agents;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Certificates;

/// <summary>
/// Marks a connection that was served the public IP certificate: its connection's features carry it, so its requests
/// see it (a node code then names no certificate: a short-lived, publicly trusted leaf is never pinned).
/// </summary>
internal sealed class PublicIpCertificateFeature
{
    public static PublicIpCertificateFeature Instance { get; } = new();
}

/// <summary>A Let's Encrypt certificate ready to serve: the leaf with its key, its chain, and what it is for.</summary>
internal sealed class ServedPublicIpCertificate : IDisposable
{
    private static readonly TimeSpan s_contextRetry = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private SslStreamCertificateContext? _context;
    private long _failedAt;

    private ServedPublicIpCertificate(X509Certificate2 leaf, X509Certificate2Collection intermediates, IPAddress address)
    {
        Leaf = leaf;
        Intermediates = intermediates;
        Address = address;
        NotBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        NotAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(leaf.RawData));
    }

    public X509Certificate2 Leaf { get; }

    public X509Certificate2Collection Intermediates { get; }

    public IPAddress Address { get; }

    public DateTimeOffset NotBefore { get; }

    public DateTimeOffset NotAfter { get; }

    public string Sha256 { get; }

    /// <summary>
    /// What a handshake serves: the leaf with its chain, so browsers and agents fetch no missing intermediate. Made at the
    /// first handshake that needs it, offline (nothing is downloaded); on Windows, .NET puts the intermediates in the
    /// panel account's own intermediate store (CurrentUser\CA) for SChannel to send them, which trusts nothing. A failure
    /// is retried a minute later at the earliest, and the endpoint's own certificate is served meanwhile; <paramref name="error"/>
    /// is the failure of this call only.
    /// </summary>
    public bool TryGetContext(out SslStreamCertificateContext context, out Exception? error)
    {
        error = null;
        if (Volatile.Read(ref _context) is { } made)
        {
            context = made;
            return true;
        }

        lock (_lock)
        {
            if (_context is null && (_failedAt == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_failedAt) >= s_contextRetry))
            {
                try
                {
                    Volatile.Write(ref _context, SslStreamCertificateContext.Create(Leaf, Intermediates, offline: true));
                }
                catch (CryptographicException ex)
                {
                    _failedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    error = ex;
                }
            }

            context = _context!;
            return _context is not null;
        }
    }

    /// <summary>
    /// The chain (leaf first) with the leaf's private key. The key goes through a PKCS#12 into a key container of its own:
    /// SChannel cannot sign a handshake with a key that only lives in memory. The container goes with <see cref="Dispose"/>.
    /// </summary>
    public static ServedPublicIpCertificate Create(IReadOnlyList<X509Certificate2> chain, ECDsa key, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(key);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var publicOnly = X509CertificateLoader.LoadCertificate(chain[0].RawData);
        using var withKey = publicOnly.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pkcs12, password);
        try
        {
            var leaf = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.DefaultKeySet);
            var intermediates = new X509Certificate2Collection();
            foreach (var certificate in chain.Skip(1))
            {
                intermediates.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
            }

            return new ServedPublicIpCertificate(leaf, intermediates, PublicAddresses.Normalize(address));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    public void Dispose()
    {
        Leaf.Dispose();
        foreach (var certificate in Intermediates)
        {
            certificate.Dispose();
        }
    }
}

/// <summary>The public IP certificate the main endpoint serves now, if any (none while the feature is off).</summary>
internal sealed class PublicIpCertificateStore : IDisposable
{
    private readonly Lock _lock = new();
    private ServedPublicIpCertificate? _current;
    private ServedPublicIpCertificate? _previous;

    public ServedPublicIpCertificate? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Serves <paramref name="next"/> from the next handshake on. The one before stays alive one more swap, so a handshake
    /// that picked it a moment ago still completes. Null serves none and lets both go at once (their keys' containers
    /// with them): the feature was turned off or the address changed.
    /// </summary>
    public void Serve(ServedPublicIpCertificate? next)
    {
        List<ServedPublicIpCertificate> old = [];
        lock (_lock)
        {
            if (ReferenceEquals(next, _current))
            {
                return;
            }

            if (_previous is { } previous && !ReferenceEquals(previous, next))
            {
                old.Add(previous);
            }

            _previous = next is null ? null : _current;
            if (next is null && _current is { } current)
            {
                old.Add(current);
            }

            Volatile.Write(ref _current, next);
        }

        foreach (var certificate in old)
        {
            certificate.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _current?.Dispose();
            _previous?.Dispose();
            _current = null;
            _previous = null;
        }
    }
}

/// <summary>
/// Chooses the certificate of each connection on the panel's browser endpoints (docs/architecture/08, T40). Browsers send
/// no server name (SNI) for an IP address, so the choice goes by the connection: one from an internet address, to the
/// certificate's address (or to this machine's local address behind the router's port forward), that named no host gets
/// the public IP certificate with its chain while one is valid; every other one gets the endpoint's own certificate, as
/// before (the loopback, the local network, anything that named a host, and the agents' endpoint, which is never touched).
/// </summary>
internal sealed partial class PublicIpCertificateSelection(
    PublicIpCertificateStore store, IOptions<PublicIpCertificateOptions> options, TimeProvider clock, ILogger<PublicIpCertificateSelection> logger)
{
    /// <summary>Every endpoint of <c>Kestrel:Endpoints</c> but the agents' one: those that are HTTPS choose per connection.</summary>
    public void ConfigureEndpoints(KestrelConfigurationLoader loader, IConfiguration endpoints)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (var endpoint in endpoints.GetChildren())
        {
            if (!string.Equals(endpoint.Key, AgentsEndpoint.Name, StringComparison.OrdinalIgnoreCase))
            {
                loader.Endpoint(endpoint.Key, Configure);
            }
        }
    }

    /// <summary>
    /// Kestrel's configuration of an endpoint: its own certificate (and chain) stays the default, chosen per connection.
    /// An endpoint set up some other way (SNI, a client certificate, a selector of its own) is left as it is.
    /// </summary>
    internal void Configure(EndpointConfiguration endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsHttps)
        {
            return;
        }

        var https = endpoint.HttpsOptions;
        if (https.ServerCertificate is not { } certificate || https.ServerCertificateSelector is not null || https.OnAuthenticate is not null
            || https.ClientCertificateMode != ClientCertificateMode.NoCertificate || endpoint.ConfigSection.GetSection("Sni").Exists())
        {
            LogNotServed(logger, endpoint.ConfigSection.Key);
            return;
        }

        // As Kestrel makes it from the endpoint's configuration.
        var own = SslStreamCertificateContext.Create(certificate, https.ServerCertificateChain);
        var protocols = https.SslProtocols;
        endpoint.ListenOptions.UseHttps(new TlsHandshakeCallbackOptions
        {
            HandshakeTimeout = https.HandshakeTimeout,
            OnConnection = context => ValueTask.FromResult(Select(context, own, protocols)),
        });
    }

    /// <summary>
    /// Whether a connection gets <paramref name="certificate"/>: it is valid now, the client named no host (or named the
    /// certificate's address), comes from an internet address that is not this machine's own, and reached the certificate's
    /// address: that address itself, or a local address of this machine of its family (a router's port forward).
    /// </summary>
    internal static bool ShouldServe(ServedPublicIpCertificate certificate, IPAddress? remote, IPAddress? local, string? serverName, DateTimeOffset now,
        bool loopbackIsInternet = false)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (now < certificate.NotBefore || now >= certificate.NotAfter || remote is null || local is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(serverName)
            && !(IPAddress.TryParse(serverName, out var named) && PublicAddresses.Normalize(named).Equals(certificate.Address)))
        {
            return false;
        }

        remote = PublicAddresses.Normalize(remote);
        local = PublicAddresses.Normalize(local);
        if (loopbackIsInternet && IPAddress.IsLoopback(remote) && IPAddress.IsLoopback(local))
        {
            return true;
        }

        if (PublicAddresses.IsLocal(remote) || remote.Equals(local) || local.AddressFamily != certificate.Address.AddressFamily)
        {
            return false;
        }

        return local.Equals(certificate.Address) || PublicAddresses.IsLocal(local);
    }

    private SslServerAuthenticationOptions Select(TlsHandshakeCallbackContext context, SslStreamCertificateContext own, SslProtocols protocols)
    {
        var chosen = own;
        var connection = context.Connection;
        if (store.Current is { } current && connection is not null
            && ShouldServe(current, (connection.RemoteEndPoint as IPEndPoint)?.Address, (connection.LocalEndPoint as IPEndPoint)?.Address,
                context.ClientHelloInfo.ServerName, clock.GetUtcNow(), options.Value.TreatLoopbackAsInternet))
        {
            if (current.TryGetContext(out var served, out var error))
            {
                chosen = served;
                connection.Features.Set(PublicIpCertificateFeature.Instance);
            }
            else if (error is not null)
            {
                LogContextFailed(logger, error);
            }
        }

        return new SslServerAuthenticationOptions
        {
            ServerCertificateContext = chosen,
            EnabledSslProtocols = protocols,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            AllowRenegotiation = false,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The public IP certificate cannot be served (its chain could not be built): the endpoint's own certificate is served instead")]
    private static partial void LogContextFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The HTTPS endpoint {Endpoint} is set up with SNI, client certificates or a certificate selector: it never serves the public IP certificate")]
    private static partial void LogNotServed(ILogger logger, string endpoint);
}
