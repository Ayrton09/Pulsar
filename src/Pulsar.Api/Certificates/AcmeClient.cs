using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Certificates;

/// <summary>What kind of failure an ACME exchange ended with.</summary>
internal enum AcmeFailure
{
    /// <summary>Anything else: a refusal, a challenge that failed, an answer that was not what the protocol says.</summary>
    General,

    /// <summary>The server, or the way to it, could not be reached or did not answer in time.</summary>
    Unreachable,

    /// <summary>The server's rate limits (<c>rateLimited</c>, or 503 with Retry-After): wait as long as it says.</summary>
    RateLimited,

    /// <summary>The order's <c>replaces</c> was refused (the certificate was already replaced): order again without it.</summary>
    AlreadyReplaced,
}

/// <summary>An ACME exchange that did not end well, with a message fit to show an administrator.</summary>
internal sealed class AcmeException(string message, AcmeFailure failure = AcmeFailure.General, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public AcmeFailure Failure { get; } = failure;

    /// <summary>How long the server asked to wait (Retry-After), when it did.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The directory's addresses and what the panel needs of its metadata.</summary>
internal sealed record AcmeDirectory(Uri NewNonce, Uri NewAccount, Uri NewOrder, Uri? RenewalInfo, string? TermsOfService, IReadOnlySet<string> Profiles);

internal sealed record AcmeOrder(Uri Url, string Status, IReadOnlyList<Uri> Authorizations, Uri Finalize, Uri? Certificate, string? Profile, string? Error);

internal sealed record AcmeChallenge(string Type, Uri Url, string Token, string Status, string? Error);

internal sealed record AcmeAuthorization(string Status, string IdentifierType, string IdentifierValue, IReadOnlyList<AcmeChallenge> Challenges);

/// <summary>ARI's suggested renewal window (RFC 9773).</summary>
internal sealed record AcmeRenewalWindow(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// A small ACME client (RFC 8555) for one thing: an IP address certificate from Let's Encrypt with the <c>shortlived</c>
/// profile, validated by http-01 (docs/architecture/08, T40). Requests are JWS signed with the account's ECDSA P-256 key
/// (ES256). It talks only to the directory's own host, over HTTPS: every address it is given (by the directory, a
/// <c>Location</c> header, an order or an authorization) must be https on that host and port, and redirects are refused.
/// Answers are read strictly: bounded sizes (64 KiB), each request 30 seconds at most, JSON of a bounded depth with the
/// members the protocol says, of the types it says. Nonces come from every answer and are asked for when none is left; a
/// <c>badNonce</c> refusal is retried with the fresh nonce it carries, three times at most.
/// </summary>
internal sealed class AcmeClient : IDisposable
{
    public const string ShortLivedProfile = "shortlived";
    public const int MaxJsonBytes = 64 * 1024;
    public const int MaxChainBytes = 64 * 1024;
    public const int MaxUrlLength = PublicIpCertificateSettings.MaxUrlLength;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const string ErrorPrefix = "urn:ietf:params:acme:error:";
    private const string NewOrderSubject = "the new order";
    private const int MaxBadNonceRetries = 3;
    private static readonly JsonDocumentOptions s_json = new() { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    private readonly HttpClient _http;
    private readonly Uri _directoryUrl;
    private readonly ECDsa _accountKey;
    private readonly string _jwk;
    private string? _nonce;
    private string? _kid;
    private AcmeDirectory? _directory;

    public AcmeClient(HttpMessageHandler handler, Uri directoryUrl, ECDsa accountKey)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(directoryUrl);
        ArgumentNullException.ThrowIfNull(accountKey);
        if (directoryUrl.Scheme != Uri.UriSchemeHttps || directoryUrl.UserInfo.Length > 0)
        {
            throw new ArgumentException("The ACME directory must be an https address.", nameof(directoryUrl));
        }

        _directoryUrl = directoryUrl;
        _accountKey = accountKey;
        _jwk = Jwk(accountKey);
        Thumbprint = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(_jwk)));
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pulsar-Panel", "1.0"));
        _http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
    }

    /// <summary>The account's URL once registered or given (its key id in signed requests).</summary>
    public string? AccountUrl => _kid;

    /// <summary>
    /// The handler the panel sends ACME requests through: no redirects, no cookies, no decompression, bounded headers and
    /// connection time; the platform's certificate validation.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 64 * 1024,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>The JWK thumbprint of the account key (RFC 7638), which a key authorization ends with.</summary>
    public string Thumbprint { get; }

    /// <summary>What the http-01 challenge serves for <paramref name="token"/> (RFC 8555, section 8.1).</summary>
    public string KeyAuthorization(string token) => token + "." + Thumbprint;

    public void UseAccount(string accountUrl) => _kid = CheckUrl(accountUrl, "the account").AbsoluteUri;

    public async Task<AcmeDirectory> GetDirectoryAsync(CancellationToken cancellationToken)
    {
        if (_directory is not null)
        {
            return _directory;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _directoryUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await SendAsync(request, MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the directory");
        using var document = ParseJson(response, "the directory");
        var root = document.RootElement;
        string? terms = null;
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("meta", out var meta))
        {
            if (meta.ValueKind != JsonValueKind.Object)
            {
                throw Malformed("the directory");
            }

            if (meta.TryGetProperty("externalAccountRequired", out var external) && external.ValueKind == JsonValueKind.True)
            {
                throw new AcmeException("This ACME server needs an external account binding, which Pulsar does not support.");
            }

            if (meta.TryGetProperty("termsOfService", out var tos))
            {
                terms = tos.ValueKind == JsonValueKind.String && Uri.TryCreate(tos.GetString(), UriKind.Absolute, out var tosUri)
                    && tosUri.Scheme == Uri.UriSchemeHttps && tosUri.AbsoluteUri.Length <= MaxUrlLength ? tosUri.AbsoluteUri : throw Malformed("the directory");
            }

            if (meta.TryGetProperty("profiles", out var offered))
            {
                if (offered.ValueKind != JsonValueKind.Object)
                {
                    throw Malformed("the directory");
                }

                foreach (var profile in offered.EnumerateObject())
                {
                    profiles.Add(profile.Name);
                }
            }
        }

        _directory = new AcmeDirectory(
            RequiredUrl(root, "newNonce", "the directory"),
            RequiredUrl(root, "newAccount", "the directory"),
            RequiredUrl(root, "newOrder", "the directory"),
            root.TryGetProperty("renewalInfo", out _) ? RequiredUrl(root, "renewalInfo", "the directory") : null,
            terms,
            profiles);
        return _directory;
    }

    /// <summary>
    /// Registers the account key (or finds the account it already has), agreeing to the server's terms: the
    /// administrator who turned the feature on agreed to them. Returns the account's URL.
    /// </summary>
    public async Task<string> RegisterAccountAsync(string? contactEmail, CancellationToken cancellationToken)
    {
        var directory = await GetDirectoryAsync(cancellationToken);
        var payload = JsonPayload(writer =>
        {
            writer.WriteBoolean("termsOfServiceAgreed", true);
            WriteContact(writer, contactEmail);
        });
        var response = await PostAsync(directory.NewAccount, payload, useJwk: true, "application/json", MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the account");
        if (response.Status is not (HttpStatusCode.OK or HttpStatusCode.Created) || response.Location is null)
        {
            throw Malformed("the account");
        }

        using var document = ParseJson(response, "the account");
        if (RequiredString(document.RootElement, "status", "the account") != "valid")
        {
            throw new AcmeException("The ACME server says this account is deactivated or revoked.");
        }

        _kid = CheckUrl(response.Location, "the account").AbsoluteUri;
        return _kid;
    }

    /// <summary>Sets the account's contact (none when <paramref name="contactEmail"/> is null).</summary>
    public async Task UpdateContactAsync(string? contactEmail, CancellationToken cancellationToken)
    {
        var account = new Uri(_kid ?? throw new InvalidOperationException("No account."));
        var payload = JsonPayload(writer => WriteContact(writer, contactEmail, always: true));
        var response = await PostAsync(account, payload, useJwk: false, "application/json", MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the account");
    }

    /// <summary>A new order for one IP address with the <c>shortlived</c> profile, replacing a certificate when ARI names one.</summary>
    public async Task<AcmeOrder> NewOrderAsync(IPAddress address, string? replaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var directory = await GetDirectoryAsync(cancellationToken);
        var payload = JsonPayload(writer =>
        {
            writer.WriteStartArray("identifiers");
            writer.WriteStartObject();
            writer.WriteString("type", "ip");
            writer.WriteString("value", address.ToString());
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("profile", ShortLivedProfile);
            if (replaces is not null)
            {
                writer.WriteString("replaces", replaces);
            }
        });
        var response = await PostAsync(directory.NewOrder, payload, useJwk: false, "application/json", MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, NewOrderSubject);
        if (response.Status != HttpStatusCode.Created || response.Location is null)
        {
            throw Malformed("the order");
        }

        return ReadOrder(response, CheckUrl(response.Location, "the order"));
    }

    public async Task<AcmeOrder> GetOrderAsync(Uri order, CancellationToken cancellationToken)
    {
        var response = await PostAsync(CheckUrl(order, "the order"), null, useJwk: false, "application/json", MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the order");
        return ReadOrder(response, order);
    }

    public async Task<AcmeAuthorization> GetAuthorizationAsync(Uri authorization, CancellationToken cancellationToken)
    {
        var response = await PostAsync(CheckUrl(authorization, "the authorization"), null, useJwk: false, "application/json", MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the authorization");
        using var document = ParseJson(response, "the authorization");
        var root = document.RootElement;
        var status = RequiredString(root, "status", "the authorization");
        if (status is not ("pending" or "valid" or "invalid" or "deactivated" or "expired" or "revoked")
            || !root.TryGetProperty("identifier", out var identifier) || identifier.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("challenges", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 16)
        {
            throw Malformed("the authorization");
        }

        var challenges = new List<AcmeChallenge>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Malformed("the authorization");
            }

            var type = RequiredString(item, "type", "the authorization");
            if (type != "http-01")
            {
                continue; // other kinds (tls-alpn-01, dns-01) are not used
            }

            var token = RequiredString(item, "token", "the challenge");
            if (!IsToken(token))
            {
                throw Malformed("the challenge");
            }

            challenges.Add(new AcmeChallenge(type, RequiredUrl(item, "url", "the challenge"), token,
                RequiredString(item, "status", "the challenge"), ErrorOf(item)));
        }

        return new AcmeAuthorization(status, RequiredString(identifier, "type", "the authorization"), RequiredString(identifier, "value", "the authorization"),
            challenges);
    }

    /// <summary>Tells the server the challenge can be checked now (an empty JSON object, RFC 8555 section 7.5.1).</summary>
    public async Task RespondAsync(AcmeChallenge challenge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var response = await PostAsync(CheckUrl(challenge.Url, "the challenge"), "{}"u8.ToArray(), useJwk: false, "application/json", MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the challenge");
    }

    public async Task<AcmeOrder> FinalizeAsync(AcmeOrder order, byte[] csr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(csr);
        var payload = JsonPayload(writer => writer.WriteString("csr", Base64Url.EncodeToString(csr)));
        var response = await PostAsync(CheckUrl(order.Finalize, "the order"), payload, useJwk: false, "application/json", MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the order");
        return ReadOrder(response, order.Url);
    }

    /// <summary>The certificate chain, PEM (leaf first), as the server sends it.</summary>
    public async Task<string> DownloadCertificateAsync(Uri certificate, CancellationToken cancellationToken)
    {
        var response = await PostAsync(CheckUrl(certificate, "the certificate"), null, useJwk: false, "application/pem-certificate-chain",
            MaxChainBytes, cancellationToken);
        EnsureSuccess(response, "the certificate");
        if (!string.Equals(response.MediaType, "application/pem-certificate-chain", StringComparison.OrdinalIgnoreCase))
        {
            throw Malformed("the certificate");
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(response.Body);
        }
        catch (DecoderFallbackException ex)
        {
            throw new AcmeException("The ACME server sent a certificate that is not PEM text.", inner: ex);
        }
    }

    /// <summary>ARI's renewal window for a certificate (an unauthenticated GET, RFC 9773); null when the server has no ARI.</summary>
    public async Task<AcmeRenewalWindow?> GetRenewalInfoAsync(string certificateId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificateId);
        var directory = await GetDirectoryAsync(cancellationToken);
        if (directory.RenewalInfo is null || certificateId.Length > 256 || !certificateId.All(c => IsBase64UrlChar(c) || c == '.'))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, CheckUrl(directory.RenewalInfo.AbsoluteUri.TrimEnd('/') + "/" + certificateId, "the renewal information"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await SendAsync(request, MaxJsonBytes, cancellationToken);
        EnsureSuccess(response, "the renewal information");
        using var document = ParseJson(response, "the renewal information");
        if (!document.RootElement.TryGetProperty("suggestedWindow", out var window) || window.ValueKind != JsonValueKind.Object)
        {
            throw Malformed("the renewal information");
        }

        var start = Timestamp(RequiredString(window, "start", "the renewal information"));
        var end = Timestamp(RequiredString(window, "end", "the renewal information"));
        return end > start ? new AcmeRenewalWindow(start, end) : throw Malformed("the renewal information");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>A token as RFC 8555 makes them (base64url, at least 128 bits): nothing else is ever served on port 80.</summary>
    public static bool IsToken(ReadOnlySpan<char> token)
    {
        if (token.Length is < 22 or > 128)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!IsBase64UrlChar(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The account key's JWK with exactly the members RFC 7638 hashes, in its order.</summary>
    internal static string Jwk(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value || parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
        {
            throw new ArgumentException("The ACME account key must be ECDSA P-256.", nameof(key));
        }

        return $$"""{"crv":"P-256","kty":"EC","x":"{{Base64Url.EncodeToString(x)}}","y":"{{Base64Url.EncodeToString(y)}}"}""";
    }

    /// <summary>A flattened JWS (RFC 7515) over <paramref name="payload"/> (null: POST-as-GET, an empty payload).</summary>
    internal static byte[] Sign(ECDsa key, string jwk, Uri url, string nonce, string? kid, byte[]? payload)
    {
        using var header = new MemoryStream();
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", "ES256");
            if (kid is null)
            {
                writer.WritePropertyName("jwk");
                writer.WriteRawValue(jwk, skipInputValidation: true);
            }
            else
            {
                writer.WriteString("kid", kid);
            }

            writer.WriteString("nonce", nonce);
            writer.WriteString("url", url.AbsoluteUri);
            writer.WriteEndObject();
        }

        var protectedHeader = Base64Url.EncodeToString(header.ToArray());
        var encodedPayload = payload is null ? string.Empty : Base64Url.EncodeToString(payload);
        var signature = key.SignData(Encoding.ASCII.GetBytes(protectedHeader + "." + encodedPayload), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonPayload(writer =>
        {
            writer.WriteString("protected", protectedHeader);
            writer.WriteString("payload", encodedPayload);
            writer.WriteString("signature", Base64Url.EncodeToString(signature));
        });
    }

    private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static void WriteContact(Utf8JsonWriter writer, string? email, bool always = false)
    {
        if (email is null && !always)
        {
            return;
        }

        writer.WriteStartArray("contact");
        if (email is not null)
        {
            writer.WriteStringValue("mailto:" + email);
        }

        writer.WriteEndArray();
    }

    private static byte[] JsonPayload(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private async Task<Response> PostAsync(Uri url, byte[]? payload, bool useJwk, string accept, int maxBytes, CancellationToken cancellationToken)
    {
        if (!useJwk && _kid is null)
        {
            throw new InvalidOperationException("No account to sign with.");
        }

        for (var attempt = 0; ; attempt++)
        {
            var nonce = _nonce ?? await NewNonceAsync(cancellationToken);
            _nonce = null;
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(Sign(_accountKey, _jwk, url, nonce, useJwk ? null : _kid, payload)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            var response = await SendAsync(request, maxBytes, cancellationToken);
            if ((int)response.Status >= 400 && ProblemType(response) == "badNonce" && attempt < MaxBadNonceRetries)
            {
                continue; // the refusal carried a fresh nonce
            }

            return response;
        }
    }

    private async Task<string> NewNonceAsync(CancellationToken cancellationToken)
    {
        var directory = await GetDirectoryAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Head, directory.NewNonce);
        var response = await SendAsync(request, 0, cancellationToken);
        if (response.Status is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
        {
            EnsureSuccess(response, "a nonce");
            throw Malformed("a nonce");
        }

        var nonce = _nonce ?? throw new AcmeException("The ACME server sent no nonce.");
        _nonce = null;
        return nonce;
    }

    /// <summary>One request, its answer read whole (up to <paramref name="maxBytes"/>) within <see cref="RequestTimeout"/>.</summary>
    private async Task<Response> SendAsync(HttpRequestMessage request, int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Headers.TryGetValues("Replay-Nonce", out var nonces) && nonces.FirstOrDefault() is { } nonce
                && nonce.Length is > 0 and <= 256 && nonce.All(IsBase64UrlChar))
            {
                _nonce = nonce;
            }

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw new AcmeException($"The ACME server answered with a redirect ({status}), which Pulsar does not follow.");
            }

            // A HEAD answer has no body, whatever length it announces (that of the GET).
            var head = request.Method == HttpMethod.Head;
            if (!head && response.Content.Headers.ContentLength > maxBytes)
            {
                throw new AcmeException("The ACME server's answer is larger than Pulsar accepts.");
            }

            var body = head ? [] : await ReadBoundedAsync(response.Content, maxBytes, timeout.Token);
            return new Response(response.StatusCode, body, response.Content.Headers.ContentType?.MediaType,
                response.Headers.Location?.IsAbsoluteUri == true ? response.Headers.Location : response.Headers.Location is { } relative ? new Uri(request.RequestUri!, relative) : null,
                RetryAfterOf(response));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcmeException("The ACME server did not answer in time.", AcmeFailure.Unreachable, inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AcmeException($"Could not reach the ACME server ({ex.HttpRequestError}).", AcmeFailure.Unreachable, inner: ex);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[Math.Min(maxBytes, 16 * 1024) + 1];
        using var body = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                throw new AcmeException("The ACME server's answer is larger than Pulsar accepts.");
            }

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } value ? value < TimeSpan.Zero ? TimeSpan.Zero : value : null;
    }

    private static string? ProblemType(Response response)
    {
        if (!string.Equals(response.MediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body, s_json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String && type.GetString() is { } value && value.StartsWith(ErrorPrefix, StringComparison.Ordinal)
                ? value[ErrorPrefix.Length..]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A failed answer as an exception: the server's problem document (type and detail) when it sent one.</summary>
    private static void EnsureSuccess(Response response, string what)
    {
        var status = (int)response.Status;
        if (status is >= 200 and < 300)
        {
            return;
        }

        var type = ProblemType(response);
        string? detail = null;
        if (type is not null)
        {
            using var document = JsonDocument.Parse(response.Body, s_json);
            detail = ProblemText(document.RootElement);
        }

        var message = $"The ACME server refused {what} ({status}{(type is null ? string.Empty : ", " + type)}){(detail is null ? "." : ": " + detail)}";
        var failure = type switch
        {
            "rateLimited" => AcmeFailure.RateLimited,
            "alreadyReplaced" => AcmeFailure.AlreadyReplaced,
            _ when response.Status == HttpStatusCode.Conflict && what == NewOrderSubject => AcmeFailure.AlreadyReplaced,
            _ when response.Status == HttpStatusCode.ServiceUnavailable => response.RetryAfter is null ? AcmeFailure.Unreachable : AcmeFailure.RateLimited,
            _ => AcmeFailure.General,
        };
        throw new AcmeException(message, failure, response.RetryAfter);
    }

    /// <summary>The problem an order or a challenge carries in its <c>error</c>, if any.</summary>
    private static string? ErrorOf(JsonElement element) =>
        element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? ProblemText(error) : null;

    /// <summary>A problem document's detail (or its type), cleaned of control and format characters and cut short.</summary>
    private static string? ProblemText(JsonElement problem)
    {
        if (problem.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (problem.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
        {
            return Clean(detail.GetString()!);
        }

        return problem.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? Clean(type.GetString()!) : null;
    }

    private static string Clean(string text)
    {
        var cleaned = new string([.. text.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).Take(300)]);
        return cleaned.Trim();
    }

    private AcmeOrder ReadOrder(Response response, Uri url)
    {
        using var document = ParseJson(response, "the order");
        var root = document.RootElement;
        var status = RequiredString(root, "status", "the order");
        if (status is not ("pending" or "ready" or "processing" or "valid" or "invalid")
            || !root.TryGetProperty("authorizations", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 4)
        {
            throw Malformed("the order");
        }

        var authorizations = list.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? CheckUrl(a.GetString()!, "the order") : throw Malformed("the order")).ToList();
        Uri? certificate = root.TryGetProperty("certificate", out _) ? RequiredUrl(root, "certificate", "the order") : null;
        string? profile = root.TryGetProperty("profile", out var named) ? named.ValueKind == JsonValueKind.String ? named.GetString() : throw Malformed("the order") : null;
        if (profile is not null && profile != ShortLivedProfile)
        {
            throw new AcmeException($"The ACME server put the order on another profile ({Clean(profile)}) than shortlived.");
        }

        return new AcmeOrder(url, status, authorizations, RequiredUrl(root, "finalize", "the order"), certificate, profile,
            status == "invalid" ? ErrorOf(root) : null);
    }

    private static JsonDocument ParseJson(Response response, string what)
    {
        if (response.MediaType is not { } type || !(type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw Malformed(what);
        }

        try
        {
            var document = JsonDocument.Parse(response.Body, s_json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Malformed(what);
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw new AcmeException($"The ACME server's answer about {what} is not valid JSON.", inner: ex);
        }
    }

    private static string RequiredString(JsonElement element, string name, string what) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 4096 } text
            ? text
            : throw Malformed(what);

    private Uri RequiredUrl(JsonElement element, string name, string what) => CheckUrl(RequiredString(element, name, what), what);

    private Uri CheckUrl(string? value, string what) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) ? CheckUrl(url, what) : throw Malformed(what);

    /// <summary>Only https addresses on the directory's own host and port: the server cannot send the panel elsewhere.</summary>
    private Uri CheckUrl(Uri url, string what)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0 || url.Fragment.Length > 0
            || !string.Equals(url.IdnHost, _directoryUrl.IdnHost, StringComparison.OrdinalIgnoreCase) || url.Port != _directoryUrl.Port
            || url.AbsoluteUri.Length > MaxUrlLength)
        {
            throw new AcmeException($"The ACME server named an address for {what} on another host, or not over HTTPS: refused.");
        }

        return url;
    }

    private static DateTimeOffset Timestamp(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : throw Malformed("the renewal information");

    private static AcmeException Malformed(string what) => new($"The ACME server's answer about {what} is not what the protocol says.");

    private sealed record Response(HttpStatusCode Status, byte[] Body, string? MediaType, Uri? Location, TimeSpan? RetryAfter);
}
