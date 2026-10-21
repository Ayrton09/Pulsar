using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Security;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests;

/// <summary>
/// The panel's certificate on the account page (<c>GET /api/v1/panel/certificate</c> and <c>/download</c>), on real
/// Kestrel with real TLS: the very certificate the panel served the connection asking for it, to any signed-in user, and
/// none when the panel cannot tell which certificate its clients see.
/// </summary>
public sealed class PanelCertificateKestrelTests(KestrelPanelFactory factory) : IClassFixture<KestrelPanelFactory>
{
    private const string Describe = "/api/v1/panel/certificate";
    private const string Download = "/api/v1/panel/certificate/download";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TheCertificateThePanelServedIsDescribedAndDownloaded()
    {
        factory.StartServer();
        using var admin = await AdminAsync();
        using var served = X509CertificateLoader.LoadPkcs12FromFile(factory.CertificatePath, KestrelPanelFactory.CertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet);

        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            using (var described = await SendAsync(admin, Describe, version))
            {
                Assert.Equal(HttpStatusCode.OK, described.StatusCode);
                Assert.Equal(version, described.Version);
                var certificate = (await described.Content.ReadFromJsonAsync<PanelCertificateResponse>(AccessScenario.Json, Ct))!;
                Assert.Equal(NodeCode.CertificateHash(served.RawData), certificate.Sha256);
                Assert.Equal(["localhost"], certificate.DnsNames);
                Assert.Equal(["127.0.0.1"], certificate.IpAddresses);
                Assert.Equal(served.NotAfter.ToUniversalTime(), certificate.NotAfter.UtcDateTime);
                Assert.True(certificate.SelfSigned);
                // The tests' certificate has no Basic Constraints: not one to trust as a root (the installer's says CA=false).
                Assert.False(certificate.Trustable);
            }

            using var downloaded = await SendAsync(admin, Download, version);
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            Assert.Equal(served.RawData, await downloaded.Content.ReadAsByteArrayAsync(Ct));
            Assert.Equal("application/pkix-cert", downloaded.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("pulsar-panel.cer", downloaded.Content.Headers.ContentDisposition?.FileName);
            Assert.True(downloaded.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", Assert.Single(downloaded.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    [DatabaseFact]
    public async Task AnySignedInUserGetsItWithoutAnyPermission()
    {
        factory.StartServer();
        using var admin = await AdminAsync();
        using (var created = await admin.PostAsync("/api/v1/users", new CreateUserRequest("no-permissions", AccessScenario.Password, null, null, [])))
        {
            await AccessScenario.EnsureSuccessAsync(created);
        }

        using var user = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);
        using (var login = await user.LoginAsync("no-permissions", AccessScenario.Password))
        {
            await AccessScenario.EnsureSuccessAsync(login);
        }

        await user.GetSessionAsync();
        foreach (var path in new[] { Describe, Download })
        {
            using var response = await user.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task WithoutASessionItIsRefused()
    {
        factory.StartServer();
        using var anonymous = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);

        foreach (var path in new[] { Describe, Download })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task BehindAProxyThereIsNone()
    {
        factory.StartServer();
        using var admin = await AdminAsync();

        // A proxy (its headers say so, whether or not the panel trusts it) may have served the browser a certificate of its own.
        foreach (var header in new[] { ("X-Forwarded-For", "192.0.2.10"), ("Forwarded", "for=192.0.2.10"), ("Via", "1.1 proxy") })
        {
            foreach (var path in new[] { Describe, Download })
            {
                using var response = await SendAsync(admin, path, HttpVersion.Version11, header);
                await AssertUnavailableAsync(response);
            }
        }
    }

    private async Task<PanelClient> AdminAsync()
    {
        await factory.OnceAsync("admin", async () =>
        {
            using var setup = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);
            var token = factory.Services.GetRequiredService<InitialSetup>().IssueToken();
            using var response = await setup.PostAsync("/api/v1/auth/setup",
                new SetupRequest(token, PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword, "Administrator", null));
            await AccessScenario.EnsureSuccessAsync(response);
        });
        var admin = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);
        using var login = await admin.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword);
        await AccessScenario.EnsureSuccessAsync(login);
        await admin.GetSessionAsync();
        return admin;
    }

    private static async Task<HttpResponseMessage> SendAsync(PanelClient client, string path, Version version, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative))
        {
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        foreach (var (header, value) in headers)
        {
            request.Headers.Add(header, value);
        }

        return await client.Http.SendAsync(request, Ct);
    }

    internal static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(PanelCertificateEndpoints.UnavailableCode, problem.RootElement.GetProperty("code").GetString());
    }
}

/// <summary>Without TLS of its own (the test server, as plain HTTP and HTTP/3 are), the panel has no certificate to give.</summary>
public sealed class PanelCertificateWithoutTlsTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task WithoutItsOwnTlsThereIsNone()
    {
        using var admin = await factory.CreateAdminClientAsync();

        foreach (var path in new[] { "/api/v1/panel/certificate", "/api/v1/panel/certificate/download" })
        {
            using var response = await admin.GetAsync(path);
            await PanelCertificateKestrelTests.AssertUnavailableAsync(response);
        }
    }
}

/// <summary>What the account page is told of a certificate: its fingerprint, names, and whether to trust it as a root.</summary>
public sealed class PanelCertificateDescriptionTests
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    [Fact]
    public void TheInstallersCertificateIsDescribedAndMayBeTrusted()
    {
        using var certificate = Make(authority: false, uses: [ServerAuthentication]);

        var described = PanelCertificateEndpoints.DescribeCertificate(certificate);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), described.Sha256);
        Assert.Equal(["localhost", "PANEL"], described.DnsNames);
        Assert.Equal(["127.0.0.1", "::1", "192.168.0.54"], described.IpAddresses);
        Assert.Equal(certificate.NotAfter.ToUniversalTime(), described.NotAfter.UtcDateTime);
        Assert.True(described.SelfSigned);
        Assert.True(described.Trustable);
    }

    [Fact]
    public void OneThatCouldIssueCertificatesOrServeOtherUsesIsNotToTrust()
    {
        using var authority = Make(authority: true, uses: [ServerAuthentication]);
        using var unconstrained = Make(authority: null, uses: [ServerAuthentication]);
        using var client = Make(authority: false, uses: [ServerAuthentication, ClientAuthentication]);
        using var anyUse = Make(authority: false, uses: null);

        foreach (var certificate in new[] { authority, unconstrained, client, anyUse })
        {
            var described = PanelCertificateEndpoints.DescribeCertificate(certificate);
            Assert.True(described.SelfSigned);
            Assert.False(described.Trustable);
        }
    }

    [Fact]
    public void OneAnAuthorityIssuedIsNotSelfSigned()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Pulsar test root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request(key, authority: false, uses: [ServerAuthentication]);
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(12));

        var described = PanelCertificateEndpoints.DescribeCertificate(issued);

        Assert.False(described.SelfSigned);
        Assert.False(described.Trustable);
    }

    // Like the installer's: localhost, the machine's name, the loopback addresses and a private one, as IP addresses.
    private static X509Certificate2 Make(bool? authority, string[]? uses)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Request(key, authority, uses).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static CertificateRequest Request(ECDsa key, bool? authority, string[]? uses)
    {
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        if (authority is { } ca)
        {
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        }

        if (uses is not null)
        {
            var oids = new OidCollection();
            foreach (var use in uses)
            {
                oids.Add(new Oid(use));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        }

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName("PANEL");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        names.AddIpAddress(IPAddress.Parse("192.168.0.54"));
        request.CertificateExtensions.Add(names.Build());
        return request;
    }
}
