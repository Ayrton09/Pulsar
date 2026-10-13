using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
internal sealed class AgentsEndpoint(IConfiguration configuration, IOptions<AgentsOptions> options)
{
    public const string Name = "Agents";

    private int _port;

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
        endpoint.ListenOptions.Use(next => connection =>
        {
            connection.Features.Set(AgentsEndpointFeature.Instance);
            return next(connection);
        });
        Listening(address.Port);
    }

    /// <summary>
    /// How TLS builds the chain of a client's certificate before Kestrel accepts any: with what the handshake brought
    /// only. The panel checks the key, never the chain, and the certificate is anyone's choice before that: the URLs it
    /// names (its issuer's certificate, revocation lists, OCSP) would make the panel connect wherever they say, and hold
    /// the handshake while it does. No machine trust either: nothing in the panel's stores decides anything here.
    /// </summary>
    internal static X509ChainPolicy ClientChainPolicy() => new()
    {
        RevocationMode = X509RevocationMode.NoCheck,
        DisableCertificateDownloads = true,
        TrustMode = X509ChainTrustMode.CustomRootTrust,
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
