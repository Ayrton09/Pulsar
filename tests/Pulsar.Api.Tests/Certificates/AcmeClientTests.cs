using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pulsar.Api.Certificates;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Tests.Certificates;

/// <summary>
/// The panel's ACME client against <see cref="FakeAcmeServer"/>: an IP address certificate with the shortlived profile,
/// its http-01 challenge served by the real responder on the loopback, and every way an answer can be refused.
/// </summary>
public sealed class AcmeClientTests : IAsyncLifetime
{
    private static readonly IPAddress s_address = IPAddress.Parse("1.2.3.4");
    private readonly AcmeChallenges _challenges = new();
    private readonly ECDsa _accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private FakeAcmeServer _acme = null!;
    private AcmeChallengeListener _listener = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _acme = await FakeAcmeServer.StartAsync();
        _listener = new AcmeChallengeListener(Options.Create(new PublicIpCertificateOptions { ChallengeBindAddress = "127.0.0.1", ChallengePortOverride = 0 }),
            _challenges, NullLoggerFactory.Instance, NullLogger<AcmeChallengeListener>.Instance);
        Assert.True(await _listener.EnsureStartedAsync(Ct), _listener.Error);
        _acme.ResponderPort = _listener.ListeningPort!.Value;
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        await _acme.DisposeAsync();
        _accountKey.Dispose();
    }

    [Fact]
    public async Task AnIpCertificateWithTheShortLivedProfileIsValidatedOverHttp01AndDownloaded()
    {
        using var client = Client();
        var directory = await client.GetDirectoryAsync(Ct);
        Assert.Contains(AcmeClient.ShortLivedProfile, directory.Profiles);
        Assert.Equal("https://letsencrypt.example/documents/subscriber-agreement.pdf", directory.TermsOfService);
        var account = await client.RegisterAccountAsync("admin@example.com", Ct);

        using var issued = await IssueAsync(client);

        // The account agreed to the terms, with the contact; the order asked for the address with the profile.
        var registered = Assert.Single(_acme.NewAccounts);
        Assert.True(registered["termsOfServiceAgreed"]!.GetValue<bool>());
        Assert.Equal("mailto:admin@example.com", registered["contact"]![0]!.GetValue<string>());
        var order = Assert.Single(_acme.NewOrders);
        Assert.Equal("shortlived", order["profile"]!.GetValue<string>());
        Assert.Equal("ip", order["identifiers"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("1.2.3.4", order["identifiers"]![0]!["value"]!.GetValue<string>());
        Assert.Null(order["replaces"]);

        // The responder served the key authorization, made with the account key's thumbprint.
        var answer = Assert.Single(_acme.ValidationAnswers);
        Assert.EndsWith("." + _acme.Thumbprints[account], answer, StringComparison.Ordinal);

        // A new key for the certificate (not the account's), with the address as its only name.
        var csr = CertificateRequest.LoadSigningRequest(Assert.Single(_acme.SigningRequests), HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        Assert.Empty(csr.SubjectName.RawData.Skip(2));
        var names = new X509SubjectAlternativeNameExtension(Assert.Single(csr.CertificateExtensions, e => e.Oid?.Value == "2.5.29.17").RawData);
        Assert.Single(csr.CertificateExtensions);
        Assert.Equal(s_address, Assert.Single(names.EnumerateIPAddresses()));
        Assert.Empty(names.EnumerateDnsNames());
        Assert.False(csr.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(_accountKey.ExportSubjectPublicKeyInfo()));

        // The chain: the leaf for the address on that key, 160 hours, with its intermediate.
        Assert.Equal(2, issued.Chain.Count);
        Assert.Equal(TimeSpan.FromHours(160), issued.Leaf.NotAfter - issued.Leaf.NotBefore);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(issued.PrivateKey, out _);
        Assert.True(key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(csr.PublicKey.ExportSubjectPublicKeyInfo()));
        Assert.Equal(issued.ChainPem, AcmeIssuance.ChainPem(AcmeIssuance.ParseChain(issued.ChainPem)));

        // Nothing is answered on port 80 once the order is done: the connection is closed on arrival.
        await Assert.ThrowsAsync<HttpRequestException>(() => ChallengeStatusAsync(answer!.Split('.')[0]));
    }

    [Fact]
    public async Task AFailedChallengeSaysWhatLetsEncryptSawAndStopsServingTheToken()
    {
        using var client = await RegisteredClientAsync();
        _acme.ResponderPort = ClosedPort();

        var refused = await Assert.ThrowsAsync<AcmeException>(() => IssueAsync(client));

        Assert.Contains("could not check port 80 of 1.2.3.4", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Timeout during connect", refused.Message, StringComparison.Ordinal);
        Assert.StartsWith("unreachable", Assert.Single(_acme.ValidationAnswers), StringComparison.Ordinal);
        Assert.Equal(0, _challenges.Count);
    }

    [Fact]
    public async Task ABadNonceIsRetriedWithTheFreshOne()
    {
        using var client = await RegisteredClientAsync();
        _acme.BadNonces = 2;

        using var issued = await IssueAsync(client);

        Assert.Equal(0, _acme.BadNonces);
        Assert.Single(_acme.NewOrders);
    }

    [Fact]
    public async Task RateLimitsAreReportedWithTheirRetryAfter()
    {
        using var client = await RegisteredClientAsync();
        _acme.RateLimitOrders = TimeSpan.FromHours(3);

        var refused = await Assert.ThrowsAsync<AcmeException>(() => IssueAsync(client));

        Assert.Equal(AcmeFailure.RateLimited, refused.Failure);
        Assert.Equal(TimeSpan.FromHours(3), refused.RetryAfter);
        Assert.Contains("rateLimited", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARenewalNamesTheCertificateItReplacesAndOrdersAgainWithoutWhenRefused()
    {
        using var client = await RegisteredClientAsync();
        _acme.RefuseReplaces = true;

        using var issued = await IssueAsync(client, replaces: "aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE");

        Assert.Equal(2, _acme.NewOrders.Count);
        Assert.Equal("aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", _acme.NewOrders[0]["replaces"]!.GetValue<string>());
        Assert.Null(_acme.NewOrders[1]["replaces"]);
    }

    [Fact]
    public async Task RenewalInformationGivesTheSuggestedWindow()
    {
        using var client = Client();
        using var issued = await IssueAsync(await RegisteredAsync(client));
        var id = AcmeIssuance.RenewalInfoId(issued.Leaf);
        Assert.NotNull(id);
        Assert.Matches("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", id);

        var window = await client.GetRenewalInfoAsync(id, Ct);

        Assert.NotNull(window);
        Assert.True(window.End > window.Start);
        Assert.Null(await client.GetRenewalInfoAsync("../../etc", Ct));
    }

    [Fact]
    public async Task AnOversizedAnswerIsRefused()
    {
        _acme.Intercept = async context =>
        {
            if (context.Request.Path != "/directory")
            {
                return false;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"newNonce\":\"" + new string('a', AcmeClient.MaxJsonBytes) + "\"}");
            return true;
        };
        using var client = Client();

        var refused = await Assert.ThrowsAsync<AcmeException>(() => client.GetDirectoryAsync(Ct));

        Assert.Contains("larger than Pulsar accepts", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"newNonce\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"newNonce\": 5, \"newAccount\": \"x\", \"newOrder\": \"y\"}")]
    [InlineData("{\"newNonce\": \"{base}new-nonce\", \"newAccount\": \"{base}new-account\", \"newOrder\": \"https://elsewhere.example/new-order\"}")]
    [InlineData("{\"newNonce\": \"{base}new-nonce\", \"newAccount\": \"{base}new-account\", \"newOrder\": \"http://127.0.0.1/new-order\"}")]
    [InlineData("{\"newNonce\": \"{base}new-nonce\", \"newAccount\": \"{base}new-account\", \"newOrder\": \"{base}new-order\", \"meta\": {\"externalAccountRequired\": true}}")]
    public async Task AMalformedDirectoryOrOneThatPointsElsewhereIsRefused(string json)
    {
        _acme.Intercept = async context =>
        {
            if (context.Request.Path != "/directory")
            {
                return false;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(json.Replace("{base}", _acme.BaseUrl.AbsoluteUri, StringComparison.Ordinal));
            return true;
        };
        using var client = Client();

        await Assert.ThrowsAsync<AcmeException>(() => client.GetDirectoryAsync(Ct));
    }

    [Fact]
    public async Task ARedirectIsNotFollowed()
    {
        _acme.Intercept = context =>
        {
            if (context.Request.Path != "/directory")
            {
                return Task.FromResult(false);
            }

            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = "https://elsewhere.example/directory";
            return Task.FromResult(true);
        };
        using var client = Client();

        var refused = await Assert.ThrowsAsync<AcmeException>(() => client.GetDirectoryAsync(Ct));

        Assert.Contains("redirect", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedOrderIsRefused()
    {
        using var client = await RegisteredClientAsync();
        _acme.Intercept = async context =>
        {
            if (context.Request.Path != "/new-order")
            {
                return false;
            }

            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.Headers.Location = new Uri(_acme.BaseUrl, "order/99").AbsoluteUri;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\": \"pending\", \"authorizations\": [], \"finalize\": \"x\"}");
            return true;
        };

        await Assert.ThrowsAsync<AcmeException>(() => IssueAsync(client));
    }

    [Fact]
    public async Task ADirectoryWithoutTheShortLivedProfileSaysSo()
    {
        _acme.OffersShortLived = false;
        using var client = Client();

        var directory = await client.GetDirectoryAsync(Ct);

        Assert.DoesNotContain(AcmeClient.ShortLivedProfile, directory.Profiles);
    }

    [Fact]
    public void OnlyACertificateForTheAddressOnTheKeyWithItsIntermediateIsKept()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var authority = CertificateAuthority.Create();
        using var stranger = CertificateAuthority.Create();
        var now = DateTimeOffset.UtcNow;
        var trust = ChainTrust.Only(new X509Certificate2Collection(authority.Root));

        var good = authority.IssueChain(key, s_address, TimeSpan.FromHours(160));
        AcmeIssuance.Verify(good, key, s_address, now, trust);

        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify(good, other, s_address, now, trust));
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify(good, key, IPAddress.Parse("5.6.7.8"), now, trust));
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify([good[0]], key, s_address, now, trust));
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify([good[0], stranger.Intermediate], key, s_address, now, trust));
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify(good, key, s_address, now.AddHours(161), trust));
        var expired = authority.IssueChain(key, s_address, TimeSpan.FromHours(1), now.AddHours(-2));
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify(expired, key, s_address, now, trust));
    }

    [Fact]
    public void ProductionMustChainToATrustedRootAndOnlyStagingMayNot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var authority = CertificateAuthority.Create();
        using var stranger = CertificateAuthority.Create();
        var now = DateTimeOffset.UtcNow;
        var chain = authority.IssueChain(key, s_address, TimeSpan.FromHours(160));

        // The roots it must lead to are the CA's own: another CA's, or none, is an untrusted root. (Custom roots only: no store.)
        AcmeIssuance.Verify(chain, key, s_address, now, ChainTrust.Only(new X509Certificate2Collection(authority.Root)));
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify(chain, key, s_address, now, ChainTrust.Only(new X509Certificate2Collection(stranger.Root))));
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify(chain, key, s_address, now, ChainTrust.Only(new X509Certificate2Collection())));

        // Staging's roots are trusted by nothing: it is accepted there, with the signature of its intermediate still checked.
        var staging = new ChainTrust(AllowUntrustedRoot: true, Roots: new X509Certificate2Collection());
        AcmeIssuance.Verify(chain, key, s_address, now, staging);
        Assert.Throws<AcmeException>(() => AcmeIssuance.Verify([chain[0], stranger.Intermediate], key, s_address, now, staging));
        Assert.Same(ChainTrust.Staging, ChainTrust.For(AcmeDirectoryKind.Staging, null));
        Assert.Same(ChainTrust.Platform, ChainTrust.For(AcmeDirectoryKind.Production, null));
        Assert.False(ChainTrust.For(AcmeDirectoryKind.Production, new X509Certificate2Collection()).AllowUntrustedRoot);
    }

    [Fact]
    public async Task AnAnswerCutOffAfterItsHeadersIsAnAcmeException()
    {
        _acme.Intercept = async context =>
        {
            if (context.Request.Path != "/directory")
            {
                return false;
            }

            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 500;
            await context.Response.WriteAsync("{\"newNonce\":");
            await context.Response.Body.FlushAsync();
            context.Abort();
            return true;
        };
        using var client = Client();

        var refused = await Assert.ThrowsAsync<AcmeException>(() => client.GetDirectoryAsync(Ct));

        Assert.Equal(AcmeFailure.Unreachable, refused.Failure);
    }

    [Theory]
    [InlineData("{\"newNonce\":\"\\xFF\\xFE\",\"newAccount\":\"a\",\"newOrder\":\"b\"}")]
    [InlineData("{\"newNonce\":\"{base}new-nonce\",\"newAccount\":\"{base}new-account\",\"newOrder\":\"{base}new-order\",\"meta\":{\"profiles\":{\"\\xC3\":\"x\"}}}")]
    [InlineData("{\"newNonce\":\"{base}new-nonce\",\"newAccount\":\"{base}new-account\",\"newOrder\":\"{base}new-order\",\"meta\":{\"termsOfService\":\"\\xFF\"}}")]
    public async Task InvalidUtf8InAStringIsAnAcmeExceptionNotAnInvalidOperation(string json)
    {
        _acme.Intercept = async context =>
        {
            if (context.Request.Path != "/directory")
            {
                return false;
            }

            context.Response.ContentType = "application/json";
            var bytes = json.Replace("{base}", _acme.BaseUrl.AbsoluteUri, StringComparison.Ordinal).Split("\\x")
                .SelectMany((part, index) => index == 0 ? Encoding.UTF8.GetBytes(part) : [Convert.ToByte(part[..2], 16), .. Encoding.UTF8.GetBytes(part[2..])]).ToArray();
            await context.Response.Body.WriteAsync(bytes);
            return true;
        };
        using var client = Client();

        await Assert.ThrowsAsync<AcmeException>(() => client.GetDirectoryAsync(Ct));
    }

    [Theory]
    [InlineData("urn:ietf:params:acme:error:bad\\nFORGED: a log line")]
    [InlineData("urn:ietf:params:acme:error:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task AProblemTypeThatIsNotAnErrorNameIsNotPutInTheMessage(string type)
    {
        using var client = await RegisteredClientAsync();
        _acme.Intercept = async context =>
        {
            if (context.Request.Path != "/new-order")
            {
                return false;
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync("{\"type\":\"" + type + "\",\"detail\":\"nope\"}");
            return true;
        };

        var refused = await Assert.ThrowsAsync<AcmeException>(() => IssueAsync(client));

        Assert.DoesNotContain("FORGED", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaaaaa", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n")]
    [InlineData("junk\n{leaf}")]
    [InlineData("{leaf}{leaf}{leaf}{leaf}{leaf}")]
    [InlineData("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n")]
    public void AChainThatIsNotOneToFourPemCertificatesIsRefused(string pem)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var authority = CertificateAuthority.Create();
        var leaf = AcmeIssuance.ChainPem([authority.IssueChain(key, s_address, TimeSpan.FromHours(1))[0]]);

        Assert.Throws<AcmeException>(() => AcmeIssuance.ParseChain(pem.Replace("{leaf}", leaf, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheSignedRequestIsAFlattenedJwsWithES256()
    {
        var url = new Uri("https://acme.example/new-order");
        var jwk = AcmeClient.Jwk(_accountKey);

        var body = AcmeClient.Sign(_accountKey, jwk, url, "nonce-1", "https://acme.example/acct/1", Encoding.UTF8.GetBytes("{}"));

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;
        var header = System.Text.Json.JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(root.GetProperty("protected").GetString()!)).RootElement;
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("nonce-1", header.GetProperty("nonce").GetString());
        Assert.Equal(url.AbsoluteUri, header.GetProperty("url").GetString());
        Assert.Equal("https://acme.example/acct/1", header.GetProperty("kid").GetString());
        Assert.False(header.TryGetProperty("jwk", out _));
        Assert.Equal("e30", root.GetProperty("payload").GetString());
        var signature = System.Buffers.Text.Base64Url.DecodeFromChars(root.GetProperty("signature").GetString()!);
        Assert.Equal(64, signature.Length);
        Assert.True(_accountKey.VerifyData(Encoding.ASCII.GetBytes(root.GetProperty("protected").GetString() + ".e30"), signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.StartsWith("{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"", jwk, StringComparison.Ordinal);
    }

    private AcmeClient Client() => new(_acme.Handler(), _acme.DirectoryUrl, _accountKey);

    private async Task<AcmeClient> RegisteredClientAsync() => await RegisteredAsync(Client());

    private static async Task<AcmeClient> RegisteredAsync(AcmeClient client)
    {
        await client.RegisterAccountAsync(null, Ct);
        return client;
    }

    private Task<IssuedCertificate> IssueAsync(AcmeClient client, string? replaces = null) =>
        AcmeIssuance.IssueAsync(client, s_address, replaces, _challenges, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10),
            ChainTrust.Only(new X509Certificate2Collection(_acme.Authority.Root)), TimeProvider.System, Ct);

    private async Task<HttpStatusCode> ChallengeStatusAsync(string token)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{_listener.ListeningPort}{AcmeChallengeListener.PathPrefix}{token}"), Ct);
        return response.StatusCode;
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
