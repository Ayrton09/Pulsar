using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;
using Pulsar.Api.Certificates;
using Pulsar.Api.Http;
using Pulsar.Contracts;

namespace Pulsar.Api.Security;

/// <summary>The TLS certificate the panel served the request that asks for it (docs/guides/install.md, "Connecting from another PC").</summary>
/// <param name="Sha256">The lowercase hex SHA-256 of its DER encoding: what browsers show as its SHA-256 fingerprint.</param>
/// <param name="DnsNames">The DNS names its Subject Alternative Name lists.</param>
/// <param name="IpAddresses">The IP addresses its Subject Alternative Name lists.</param>
/// <param name="NotAfter">When it expires.</param>
/// <param name="SelfSigned">It is its own issuer: nothing vouches for it but itself, so browsers warn until they trust it.</param>
/// <param name="Trustable">
/// A self-signed web server's own, as the installer makes it (Basic Constraints CA=false, server authentication alone): a
/// browser that trusts it as a root trusts it for the names it lists and nothing else. False for one that could issue
/// certificates for other sites, or is for more than a web server.
/// </param>
public sealed record PanelCertificateResponse(
    string Sha256,
    IReadOnlyList<string> DnsNames,
    IReadOnlyList<string> IpAddresses,
    DateTimeOffset NotAfter,
    bool SelfSigned,
    bool Trustable);

/// <summary>
/// The panel's own TLS certificate, for any signed-in user (docs/architecture/08): what the browser was served on this very
/// connection, which every TLS client receives anyway, to download and trust once in the browsers of a home network,
/// checking its fingerprint against the one the installer showed. 404 when the panel cannot tell which certificate its
/// clients see (<see cref="ServedCertificate"/>), and 404 <see cref="PubliclyTrustedCode"/> when the connection got the
/// public IP certificate (Let's Encrypt, docs/architecture/08 T40): every browser trusts it already, and a leaf replaced
/// every few days is nothing to install or trust as a root.
/// </summary>
public static class PanelCertificateEndpoints
{
    public const string FileName = "pulsar-panel.cer";

    public const string ContentType = "application/pkix-cert";

    public const string UnavailableCode = "panel.certificate_unavailable";

    public const string PubliclyTrustedCode = "panel.certificate_publicly_trusted";

    // id-kp-serverAuth: the only use the installer's certificate has.
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private const string SubjectAlternativeName = "2.5.29.17";

    public static RouteGroupBuilder MapPanelCertificateEndpoints(this RouteGroupBuilder api)
    {
        var certificate = api.MapGroup("/panel/certificate").WithTags("Panel");
        certificate.MapGet("/", Describe).RequireSignedInUser();
        certificate.MapGet("/download", Download).RequireSignedInUser()
            .Produces(StatusCodes.Status200OK, contentType: ContentType);
        return api;
    }

    private static Results<Ok<PanelCertificateResponse>, ProblemHttpResult> Describe(HttpContext http)
    {
        if (ServedCertificate.IsPublicIpCertificate(http))
        {
            return PubliclyTrusted();
        }

        if (ServedCertificate.Der(http) is not { } der)
        {
            return Unavailable();
        }

        using var certificate = X509CertificateLoader.LoadCertificate(der);
        return TypedResults.Ok(DescribeCertificate(certificate));
    }

    private static Results<FileContentHttpResult, ProblemHttpResult> Download(HttpContext http)
    {
        if (ServedCertificate.IsPublicIpCertificate(http))
        {
            return PubliclyTrusted();
        }

        if (ServedCertificate.Der(http) is not { } der)
        {
            return Unavailable();
        }

        // Its own file each time: a renewed certificate is served from the next connection on.
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(der, ContentType, FileName);
    }

    /// <summary>What <see cref="PanelCertificateResponse"/> says of <paramref name="certificate"/>.</summary>
    internal static PanelCertificateResponse DescribeCertificate(X509Certificate2 certificate)
    {
        var dns = new List<string>();
        var addresses = new List<string>();
        if (certificate.Extensions[SubjectAlternativeName] is { } extension)
        {
            try
            {
                var names = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
                dns.AddRange(names.EnumerateDnsNames());
                addresses.AddRange(names.EnumerateIPAddresses().Select(address => address.ToString()));
            }
            catch (CryptographicException)
            {
                // Names it cannot read are not shown; the fingerprint still identifies it.
                dns.Clear();
                addresses.Clear();
            }
        }

        var selfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        return new PanelCertificateResponse(NodeCode.CertificateHash(certificate.RawData), dns, addresses,
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero), selfSigned, selfSigned && IsWebServersOwn(certificate));
    }

    // Basic Constraints that say CA=false, and an Extended Key Usage of server authentication alone, each extension once.
    private static bool IsWebServersOwn(X509Certificate2 certificate)
    {
        var extensions = certificate.Extensions.Cast<X509Extension>().ToList();
        if (extensions.GroupBy(e => e.Oid?.Value).Any(group => group.Count() > 1))
        {
            return false;
        }

        return extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is { CertificateAuthority: false }
            && extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault() is { } uses
            && uses.EnhancedKeyUsages.Count == 1 && uses.EnhancedKeyUsages[0].Value == ServerAuthentication;
    }

    private static ProblemHttpResult PubliclyTrusted() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, PubliclyTrustedCode,
            "This connection uses the panel's Let's Encrypt certificate, which every browser trusts already: there is nothing to download or install.",
            "On the local network the panel serves its own certificate: open it there to download that one.");

    private static ProblemHttpResult Unavailable() =>
        ApiProblems.Create(StatusCodes.Status404NotFound, UnavailableCode,
            "This connection did not reach the panel's own HTTPS: there is no certificate of the panel's to give.",
            "Open the panel at its own address (not through a proxy, and over HTTPS) to download its certificate.");
}

/// <summary>
/// The certificate Kestrel presented in this connection's TLS handshake (for the host the client asked for), when the
/// client saw it: none over plain HTTP, over HTTP/3 (Kestrel does not expose QUIC's), and when a proxy forwarded the
/// request, which may terminate TLS with a certificate of its own. A proxy that re-encrypts without saying so gets
/// Kestrel's certificate, which is then not the one its clients see. None either when the handshake served the public
/// IP certificate (Let's Encrypt): it is never pinned nor offered as the panel's own (docs/architecture/08, T40).
/// </summary>
internal static class ServedCertificate
{
    // Headers a proxy in front of the panel adds: whatever certificate it serves, Kestrel's is not the one clients see.
    private static readonly string[] s_proxyHeaders =
    [
        ForwardedHeadersDefaults.XForwardedForHeaderName,
        ForwardedHeadersDefaults.XForwardedProtoHeaderName,
        ForwardedHeadersDefaults.XForwardedHostHeaderName,
        ForwardedHeadersDefaults.XForwardedPrefixHeaderName,
        ForwardedHeadersDefaults.XOriginalForHeaderName,
        ForwardedHeadersDefaults.XOriginalProtoHeaderName,
        ForwardedHeadersDefaults.XOriginalHostHeaderName,
        ForwardedHeadersDefaults.XOriginalPrefixHeaderName,
        "Forwarded",
        HeaderNames.Via,
    ];

    /// <summary>Its DER encoding (a copy), or null when the client may not have seen it.</summary>
    public static byte[]? Der(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (s_proxyHeaders.Any(http.Request.Headers.ContainsKey) || IsPublicIpCertificate(http))
        {
            return null;
        }

        return http.Features.Get<ISslStreamFeature>()?.SslStream.LocalCertificate?.GetRawCertData();
    }

    /// <summary>The handshake served the public IP certificate (<see cref="PublicIpCertificateSelection"/>).</summary>
    public static bool IsPublicIpCertificate(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Features.Get<PublicIpCertificateFeature>() is not null;
    }
}
