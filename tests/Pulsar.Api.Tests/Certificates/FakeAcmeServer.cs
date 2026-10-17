using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pulsar.Api.Tests.Certificates;

/// <summary>
/// An ACME server (RFC 8555) in the test process, on the loopback over HTTPS, as Let's Encrypt behaves for IP address
/// certificates: a directory with profiles and ARI, nonces used once, JWS (ES256) checked with the account's key, orders
/// with an IP identifier and a profile, an http-01 challenge it checks by fetching the token from the panel's responder
/// on the loopback, finalization with a CSR, a chain (leaf and intermediate of a CA of its own) to download, and renewal
/// information. Never Let's Encrypt itself. <see cref="Intercept"/> lets a test answer any request its own way.
/// </summary>
public sealed class FakeAcmeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _tls;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _nonces = [];
    private readonly Dictionary<string, (ECDsa Key, string Thumbprint)> _accounts = [];
    private readonly Dictionary<int, Order> _orders = [];
    private readonly Dictionary<int, string> _certificates = [];
    private int _next;

    private FakeAcmeServer(WebApplication app, X509Certificate2 tls, CertificateAuthority authority)
    {
        _app = app;
        _tls = tls;
        Authority = authority;
    }

    public CertificateAuthority Authority { get; }

    public Uri BaseUrl { get; private set; } = null!;

    public Uri DirectoryUrl => new(BaseUrl, "directory");

    /// <summary>The responder's address the http-01 check fetches from (the panel's challenge listener on the loopback).</summary>
    public int ResponderPort { get; set; }

    /// <summary>Where to read <see cref="ResponderPort"/> from when the responder starts later (the panel's own listener).</summary>
    public Func<int?>? ResponderPortSource { get; set; }

    public string? ResponderHost { get; set; } = "127.0.0.1";

    /// <summary>Answers a request itself when it returns true (malformed, oversized, redirected answers...).</summary>
    public Func<HttpContext, Task<bool>>? Intercept { get; set; }

    public bool OffersShortLived { get; set; } = true;

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(160);

    /// <summary>The next signed requests answered with badNonce (and a fresh nonce).</summary>
    public int BadNonces { get; set; }

    /// <summary>An order with <c>replaces</c> is refused as already replaced.</summary>
    public bool RefuseReplaces { get; set; }

    /// <summary>newOrder answers rateLimited with this Retry-After, when set.</summary>
    public TimeSpan? RateLimitOrders { get; set; }

    public List<JsonObject> NewOrders { get; } = [];

    public List<JsonObject> NewAccounts { get; } = [];

    public List<byte[]> SigningRequests { get; } = [];

    public List<string?> ValidationAnswers { get; } = [];

    public int SignedRequests { get; private set; }

    /// <summary>The account keys' JWK thumbprints, by account URL.</summary>
    public IReadOnlyDictionary<string, string> Thumbprints
    {
        get
        {
            lock (_lock)
            {
                return _accounts.ToDictionary(a => a.Key, a => a.Value.Thumbprint);
            }
        }
    }

    public static async Task<FakeAcmeServer> StartAsync()
    {
        var tls = CertificateAuthority.LoopbackServerCertificate();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l =>
        {
            l.Protocols = HttpProtocols.Http1AndHttp2;
            l.UseHttps(tls);
        }));
        var app = builder.Build();
        var server = new FakeAcmeServer(app, tls, CertificateAuthority.Create());
        app.Run(server.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.BaseUrl = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal) + "/");
        return server;
    }

    /// <summary>A handler that trusts this server's TLS certificate only (what the panel's ACME client is given in tests).</summary>
    public HttpMessageHandler Handler()
    {
        var thumbprint = _tls.Thumbprint;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase);
        return handler;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _tls.Dispose();
        Authority.Dispose();
        foreach (var (key, _) in _accounts.Values)
        {
            key.Dispose();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (Intercept is { } intercept && await intercept(context))
        {
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (path == "/directory" && HttpMethods.IsGet(context.Request.Method))
        {
            await DirectoryAsync(context);
            return;
        }

        if (path == "/new-nonce")
        {
            context.Response.Headers["Replay-Nonce"] = NewNonce();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = HttpMethods.IsHead(context.Request.Method) ? 200 : 204;
            return;
        }

        if (path.StartsWith("/renewal-info/", StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method))
        {
            // After the half-life: the panel's own timing holds (a window before it would move the renewal earlier).
            var start = DateTimeOffset.UtcNow.AddHours(120);
            await JsonAsync(context, 200, new JsonObject
            {
                ["suggestedWindow"] = new JsonObject { ["start"] = start.ToString("O"), ["end"] = start.AddHours(2).ToString("O") },
            });
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = 405;
            return;
        }

        var signed = await VerifyAsync(context);
        if (signed is null)
        {
            return;
        }

        var (account, payload) = signed.Value;
        var segments = path.Trim('/').Split('/');
        switch (segments[0])
        {
            case "new-account":
                await NewAccountAsync(context, account!, payload);
                break;
            case "acct":
                await JsonAsync(context, 200, new JsonObject { ["status"] = "valid" });
                break;
            case "new-order":
                await NewOrderAsync(context, payload!.AsObject());
                break;
            case "order":
                await OrderAsync(context, int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture));
                break;
            case "authz":
                await AuthorizationAsync(context, int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture));
                break;
            case "chall":
                await ChallengeAsync(context, int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture), account!);
                break;
            case "finalize":
                await FinalizeAsync(context, int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture), payload!.AsObject());
                break;
            case "cert":
                await CertificateAsync(context, int.Parse(segments[1], System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                await ProblemAsync(context, 404, "malformed", "No such resource.");
                break;
        }
    }

    private Task DirectoryAsync(HttpContext context)
    {
        var profiles = new JsonObject { ["classic"] = "https://example.invalid/classic" };
        if (OffersShortLived)
        {
            profiles["shortlived"] = "https://example.invalid/shortlived";
        }

        return JsonAsync(context, 200, new JsonObject
        {
            ["newNonce"] = Url("new-nonce"),
            ["newAccount"] = Url("new-account"),
            ["newOrder"] = Url("new-order"),
            ["renewalInfo"] = Url("renewal-info"),
            ["revokeCert"] = Url("revoke-cert"),
            ["Xy7Zz"] = "https://community.letsencrypt.org/t/adding-random-entries-to-the-directory/33417",
            ["meta"] = new JsonObject
            {
                ["termsOfService"] = "https://letsencrypt.example/documents/subscriber-agreement.pdf",
                ["website"] = "https://letsencrypt.example",
                ["profiles"] = profiles,
            },
        });
    }

    private async Task NewAccountAsync(HttpContext context, string account, JsonNode? payload)
    {
        lock (_lock)
        {
            NewAccounts.Add(payload!.AsObject().DeepClone().AsObject());
        }

        context.Response.Headers.Location = account;
        await JsonAsync(context, 201, new JsonObject { ["status"] = "valid", ["orders"] = Url("orders/1") });
    }

    private async Task NewOrderAsync(HttpContext context, JsonObject payload)
    {
        lock (_lock)
        {
            NewOrders.Add(payload.DeepClone().AsObject());
        }

        if (RateLimitOrders is { } wait)
        {
            context.Response.Headers.RetryAfter = ((int)wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await ProblemAsync(context, 429, "rateLimited", "too many new orders recently");
            return;
        }

        if (RefuseReplaces && payload["replaces"] is not null)
        {
            await ProblemAsync(context, 409, "alreadyReplaced", "the certificate was already replaced");
            return;
        }

        var identifiers = payload["identifiers"]!.AsArray();
        var profile = payload["profile"]?.GetValue<string>();
        if (profile != "shortlived" || identifiers.Count != 1 || identifiers[0]!["type"]!.GetValue<string>() != "ip")
        {
            await ProblemAsync(context, 400, "invalidProfile", "IP address identifiers need the shortlived profile");
            return;
        }

        int id;
        lock (_lock)
        {
            id = ++_next;
            _orders[id] = new Order(IPAddress.Parse(identifiers[0]!["value"]!.GetValue<string>()), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
        }

        context.Response.Headers.Location = Url($"order/{id}");
        await JsonAsync(context, 201, OrderJson(id));
    }

    private Task OrderAsync(HttpContext context, int id) => JsonAsync(context, 200, OrderJson(id));

    private Task AuthorizationAsync(HttpContext context, int id)
    {
        Order order;
        lock (_lock)
        {
            order = _orders[id];
        }

        var challenge = new JsonObject
        {
            ["type"] = "http-01",
            ["url"] = Url($"chall/{id}"),
            ["token"] = order.Token,
            ["status"] = order.ChallengeStatus,
        };
        if (order.Error is { } error)
        {
            challenge["error"] = new JsonObject { ["type"] = "urn:ietf:params:acme:error:connection", ["detail"] = error };
        }

        return JsonAsync(context, 200, new JsonObject
        {
            ["identifier"] = new JsonObject { ["type"] = "ip", ["value"] = order.Address.ToString() },
            ["status"] = order.AuthorizationStatus,
            ["expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            ["challenges"] = new JsonArray(
                challenge,
                new JsonObject { ["type"] = "tls-alpn-01", ["url"] = Url($"chall/{id}/alpn"), ["token"] = order.Token, ["status"] = "pending" }),
        });
    }

    /// <summary>Checks the challenge as Let's Encrypt does: fetches the token over HTTP and compares the key authorization.</summary>
    private async Task ChallengeAsync(HttpContext context, int id, string account)
    {
        Order order;
        string thumbprint;
        lock (_lock)
        {
            order = _orders[id];
            thumbprint = _accounts[account].Thumbprint;
        }

        string? answer = null;
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(new Uri($"http://{ResponderHost}:{ResponderPortSource?.Invoke() ?? ResponderPort}/.well-known/acme-challenge/{order.Token}"));
            answer = response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/plain"
                ? await response.Content.ReadAsStringAsync()
                : $"HTTP {(int)response.StatusCode}";
        }
        catch (HttpRequestException ex)
        {
            answer = "unreachable: " + ex.HttpRequestError;
        }

        lock (_lock)
        {
            ValidationAnswers.Add(answer);
            var valid = answer == order.Token + "." + thumbprint;
            order.AuthorizationStatus = valid ? "valid" : "invalid";
            order.ChallengeStatus = valid ? "valid" : "invalid";
            order.Status = valid ? "ready" : "invalid";
            order.Error = valid ? null : $"Fetching http://{order.Address}/.well-known/acme-challenge/{order.Token}: Timeout during connect (likely firewall problem)";
        }

        await JsonAsync(context, 200, new JsonObject { ["type"] = "http-01", ["url"] = Url($"chall/{id}"), ["token"] = order.Token, ["status"] = "processing" });
    }

    private async Task FinalizeAsync(HttpContext context, int id, JsonObject payload)
    {
        Order order;
        lock (_lock)
        {
            order = _orders[id];
        }

        if (order.Status != "ready")
        {
            await ProblemAsync(context, 403, "orderNotReady", "the order is not ready");
            return;
        }

        var der = Base64Url.DecodeFromChars(payload["csr"]!.GetValue<string>());
        lock (_lock)
        {
            SigningRequests.Add(der);
        }

        var request = CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        var pem = Authority.Issue(request, order.Address, Lifetime);
        lock (_lock)
        {
            _certificates[id] = pem;
            order.Status = "valid";
        }

        await JsonAsync(context, 200, OrderJson(id));
    }

    private async Task CertificateAsync(HttpContext context, int id)
    {
        string pem;
        lock (_lock)
        {
            pem = _certificates[id];
        }

        context.Response.ContentType = "application/pem-certificate-chain";
        context.Response.Headers["Replay-Nonce"] = NewNonce();
        await context.Response.WriteAsync(pem);
    }

    private JsonObject OrderJson(int id)
    {
        Order order;
        lock (_lock)
        {
            order = _orders[id];
        }

        var json = new JsonObject
        {
            ["status"] = order.Status,
            ["expires"] = DateTimeOffset.UtcNow.AddHours(8).ToString("O"),
            ["identifiers"] = new JsonArray(new JsonObject { ["type"] = "ip", ["value"] = order.Address.ToString() }),
            ["profile"] = "shortlived",
            ["authorizations"] = new JsonArray(Url($"authz/{id}")),
            ["finalize"] = Url($"finalize/{id}"),
        };
        if (order.Status == "valid")
        {
            json["certificate"] = Url($"cert/{id}");
        }

        if (order.Status == "invalid")
        {
            json["error"] = new JsonObject { ["type"] = "urn:ietf:params:acme:error:unauthorized", ["detail"] = order.Error };
        }

        return json;
    }

    /// <summary>The JWS of a request: its nonce (once), its URL, ES256 over the account's key; null when refused.</summary>
    private async Task<(string? Account, JsonNode? Payload)?> VerifyAsync(HttpContext context)
    {
        if (context.Request.ContentType != "application/jose+json")
        {
            await ProblemAsync(context, 415, "malformed", "JWS needed");
            return null;
        }

        using var body = await JsonDocument.ParseAsync(context.Request.Body);
        var root = body.RootElement;
        var protectedText = root.GetProperty("protected").GetString()!;
        var payloadText = root.GetProperty("payload").GetString()!;
        var signature = Base64Url.DecodeFromChars(root.GetProperty("signature").GetString()!);
        using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(protectedText));
        var h = header.RootElement;
        var nonce = h.GetProperty("nonce").GetString()!;
        bool fresh;
        lock (_lock)
        {
            SignedRequests++;
            fresh = _nonces.Remove(nonce);
            if (fresh && BadNonces > 0)
            {
                BadNonces--;
                fresh = false;
            }
        }

        if (!fresh)
        {
            await ProblemAsync(context, 400, "badNonce", "JWS has an invalid anti-replay nonce");
            return null;
        }

        if (h.GetProperty("alg").GetString() != "ES256" || h.GetProperty("url").GetString() != Url(context.Request.Path.Value!.TrimStart('/')))
        {
            await ProblemAsync(context, 400, "malformed", "bad JWS header");
            return null;
        }

        ECDsa key;
        string account;
        if (h.TryGetProperty("jwk", out var jwk))
        {
            var created = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!), Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!) },
            });
            var thumbprint = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $$"""{"crv":"P-256","kty":"EC","x":"{{jwk.GetProperty("x").GetString()}}","y":"{{jwk.GetProperty("y").GetString()}}"}""")));
            lock (_lock)
            {
                var existing = _accounts.FirstOrDefault(a => a.Value.Thumbprint == thumbprint);
                if (existing.Key is not null)
                {
                    created.Dispose();
                    account = existing.Key;
                }
                else
                {
                    account = Url($"acct/{++_next}");
                    _accounts[account] = (created, thumbprint);
                }

                key = _accounts[account].Key;
            }
        }
        else
        {
            account = h.GetProperty("kid").GetString()!;
            lock (_lock)
            {
                if (!_accounts.TryGetValue(account, out var known))
                {
                    key = null!;
                }
                else
                {
                    key = known.Key;
                }
            }

            if (key is null)
            {
                await ProblemAsync(context, 400, "accountDoesNotExist", "no such account");
                return null;
            }
        }

        if (!key.VerifyData(Encoding.ASCII.GetBytes(protectedText + "." + payloadText), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            await ProblemAsync(context, 400, "malformed", "JWS verification error");
            return null;
        }

        var payload = payloadText.Length == 0 ? null : JsonNode.Parse(Base64Url.DecodeFromChars(payloadText));
        return (account, payload);
    }

    private string NewNonce()
    {
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            _nonces.Add(nonce);
        }

        return nonce;
    }

    private string Url(string path) => new Uri(BaseUrl, path).AbsoluteUri;

    private async Task JsonAsync(HttpContext context, int status, JsonObject body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Replay-Nonce"] = NewNonce();
        await context.Response.WriteAsync(body.ToJsonString());
    }

    private async Task ProblemAsync(HttpContext context, int status, string type, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers["Replay-Nonce"] = NewNonce();
        await context.Response.WriteAsync(new JsonObject { ["type"] = "urn:ietf:params:acme:error:" + type, ["detail"] = detail, ["status"] = status }.ToJsonString());
    }

    private sealed class Order(IPAddress address, string token)
    {
        public IPAddress Address { get; } = address;

        public string Token { get; } = token;

        public string Status { get; set; } = "pending";

        public string AuthorizationStatus { get; set; } = "pending";

        public string ChallengeStatus { get; set; } = "pending";

        public string? Error { get; set; }
    }
}

/// <summary>A root and an intermediate of a CA of the tests' own (never trusted by the machine), issuing IP certificates.</summary>
public sealed class CertificateAuthority : IDisposable
{
    private readonly ECDsa _rootKey;
    private readonly ECDsa _intermediateKey;

    private CertificateAuthority(ECDsa rootKey, X509Certificate2 root, ECDsa intermediateKey, X509Certificate2 intermediate)
    {
        _rootKey = rootKey;
        Root = root;
        _intermediateKey = intermediateKey;
        Intermediate = intermediate;
    }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    public static CertificateAuthority Create()
    {
        // Names of their own: on Windows, serving a chain puts its intermediate in CurrentUser\CA (SslStreamCertificateContext),
        // and another CA's certificate there under the same name would be taken for this one's issuer.
        var id = Guid.NewGuid().ToString("N");
        var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest($"CN=Pulsar Tests Fake Root {id}, O=Pulsar tests", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(5));

        var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest($"CN=Pulsar Tests Fake Intermediate {id}, O=Pulsar tests", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
        intermediateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        using var issued = intermediateRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddYears(3), RandomNumberGenerator.GetBytes(16));
        var intermediate = issued.CopyWithPrivateKey(intermediateKey);
        return new CertificateAuthority(rootKey, root, intermediateKey, intermediate);
    }

    /// <summary>A leaf for <paramref name="request"/>'s key naming <paramref name="address"/>, as a PEM chain (leaf, intermediate).</summary>
    public string Issue(CertificateRequest request, IPAddress address, TimeSpan lifetime, DateTimeOffset? notBefore = null)
    {
        var leafRequest = new CertificateRequest(new X500DistinguishedName(string.Empty), request.PublicKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        leafRequest.CertificateExtensions.Add(names.Build(critical: true));
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Intermediate, true, false));
        var from = notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var leaf = leafRequest.Create(Intermediate.SubjectName, X509SignatureGenerator.CreateForECDsa(_intermediateKey), from, from + lifetime, serial);
        return PemEncoding.WriteString("CERTIFICATE", leaf.RawData) + "\n" + PemEncoding.WriteString("CERTIFICATE", Intermediate.RawData) + "\n";
    }

    /// <summary>A leaf on <paramref name="key"/> for <paramref name="address"/>, with the chain, as the store takes it.</summary>
    public List<X509Certificate2> IssueChain(ECDsa key, IPAddress address, TimeSpan lifetime, DateTimeOffset? notBefore = null)
    {
        var request = new CertificateRequest(new X500DistinguishedName(string.Empty), key, HashAlgorithmName.SHA256);
        return Pulsar.Api.Certificates.AcmeIssuance.ParseChain(Issue(request, address, lifetime, notBefore));
    }

    /// <summary>A TLS server certificate for 127.0.0.1 whose key SChannel can use (loaded back from a PKCS#12).</summary>
    public static X509Certificate2 LoopbackServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, "fake-acme"), "fake-acme");
    }

    public void Dispose()
    {
        Root.Dispose();
        Intermediate.Dispose();
        _rootKey.Dispose();
        _intermediateKey.Dispose();
    }
}
