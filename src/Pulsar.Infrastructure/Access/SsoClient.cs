using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;

namespace Pulsar.Infrastructure.Access;

/// <summary>
/// The providers' side of signing in (Pulsar 1.0.18): the authorization code flow with PKCE. Discord's account comes
/// from its API with the access token; an OpenID Connect provider's (Google's, or the administrator's) from its ID
/// token, whose signature is checked against the keys its discovery document names, with its issuer, audience, times
/// and nonce. Every address is https, no redirect is followed and every answer is read up to <see cref="MaxResponseBytes"/>.
/// Discovery documents and keys are kept an hour; keys are fetched again (at most every 5 minutes) for a key id they lack.
/// </summary>
internal sealed class SsoClient(IHttpClientFactory clients, TimeProvider clock) : ISsoClient
{
    public const string ClientName = "Pulsar.Sso";
    public const string GoogleIssuer = "https://accounts.google.com";
    public const int MaxResponseBytes = 256 * 1024;

    private const string DiscordAuthorize = "https://discord.com/oauth2/authorize";
    private const string DiscordToken = "https://discord.com/api/oauth2/token";
    private const string DiscordMe = "https://discord.com/api/users/@me";

    private static readonly TimeSpan s_cacheFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_keysRefreshAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_skew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_deadline = TimeSpan.FromSeconds(15);

    private static readonly string[] s_algorithms = ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

    private readonly ConcurrentDictionary<string, (Discovery Document, DateTimeOffset At)> _discovery = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (JsonElement Keys, DateTimeOffset At)> _keys = new(StringComparer.Ordinal);

    public async Task<Uri> AuthorizeAsync(SsoSettings settings, string redirectUri, string state, string codeChallenge, string nonce,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        if (settings.Kind == SsoKind.Discord)
        {
            query["scope"] = "identify";
            return new Uri(QueryHelpers.AddQueryString(DiscordAuthorize, query));
        }

        var discovery = await DiscoverAsync(settings, cancellationToken);
        query["scope"] = "openid profile";
        query["nonce"] = nonce;
        return new Uri(QueryHelpers.AddQueryString(discovery.AuthorizationEndpoint, query));
    }

    public async Task<SsoIdentity> RedeemAsync(SsoSettings settings, string code, string codeVerifier, string redirectUri, string nonce,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var discovery = settings.Kind == SsoKind.Discord ? null : await DiscoverAsync(settings, cancellationToken);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = settings.ClientId,
            ["code_verifier"] = codeVerifier,
        };
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, discovery?.TokenEndpoint ?? DiscordToken);
        // In the form where the provider takes it (Discord does), otherwise as HTTP Basic, the default of OpenID Connect.
        if (discovery is null || discovery.SecretInForm)
        {
            form["client_secret"] = settings.ClientSecret;
        }
        else
        {
            var basic = $"{Uri.EscapeDataString(settings.ClientId)}:{Uri.EscapeDataString(settings.ClientSecret)}";
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
        }

        tokenRequest.Content = new FormUrlEncodedContent(form);
        using var token = await SendAsync(tokenRequest, "its token endpoint", cancellationToken);
        if (settings.Kind == SsoKind.Discord)
        {
            var access = String(token.RootElement, "access_token") ?? throw Invalid("Discord answered without an access token.");
            using var meRequest = new HttpRequestMessage(HttpMethod.Get, DiscordMe);
            meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var me = await SendAsync(meRequest, "its API", cancellationToken);
            var id = String(me.RootElement, "id");
            if (id is not { Length: > 0 and <= 64 } || !id.All(char.IsAsciiDigit))
            {
                throw Invalid("Discord answered without an account id.");
            }

            return new SsoIdentity(id, Name(String(me.RootElement, "global_name") ?? String(me.RootElement, "username")));
        }

        var idToken = String(token.RootElement, "id_token") ?? throw Invalid("The provider answered without an ID token.");
        var claims = await ValidateAsync(settings, discovery!, idToken, nonce, cancellationToken);
        var subject = String(claims, "sub");
        if (subject is not { Length: > 0 and <= 255 } || subject.Any(char.IsControl))
        {
            throw Invalid("The ID token names no account.");
        }

        return new SsoIdentity(subject, Name(String(claims, "email") ?? String(claims, "preferred_username") ?? String(claims, "name")));
    }

    /// <summary>The ID token's claims, once it holds: signed by one of the issuer's keys, for this client, current, with this nonce.</summary>
    private async Task<JsonElement> ValidateAsync(SsoSettings settings, Discovery discovery, string idToken, string nonce,
        CancellationToken cancellationToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3 || idToken.Length > MaxResponseBytes)
        {
            throw Invalid("The ID token is not a signed JWT.");
        }

        JsonElement header;
        JsonElement claims;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[0])).RootElement.Clone();
            claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1])).RootElement.Clone();
            signature = WebEncoders.Base64UrlDecode(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Invalid("The ID token is not a signed JWT.", ex);
        }

        if (header.ValueKind != JsonValueKind.Object || claims.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The ID token is not a signed JWT.");
        }

        var algorithm = String(header, "alg");
        if (algorithm is null || !s_algorithms.Contains(algorithm, StringComparer.Ordinal))
        {
            throw Invalid($"The ID token is signed with an algorithm Pulsar does not take ({algorithm ?? "none"}).");
        }

        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var keyId = String(header, "kid");
        if (!Verify(await KeysAsync(discovery, keyId, refresh: false, cancellationToken), keyId, algorithm, signed, signature)
            && !Verify(await KeysAsync(discovery, keyId, refresh: true, cancellationToken), keyId, algorithm, signed, signature))
        {
            throw Invalid("The ID token's signature does not match the provider's keys.");
        }

        var issuer = String(claims, "iss");
        var issuers = settings.Kind == SsoKind.Google ? [discovery.Issuer, "accounts.google.com"] : new[] { discovery.Issuer };
        if (issuer is null || !issuers.Contains(issuer, StringComparer.Ordinal))
        {
            throw Invalid("The ID token comes from another issuer.");
        }

        var audiences = claims.TryGetProperty("aud", out var aud)
            ? aud.ValueKind == JsonValueKind.Array ? [.. aud.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null)] : new[] { String(claims, "aud") }
            : [];
        // Meant for this client; one meant for others too must say it was issued to this one.
        if (!audiences.Contains(settings.ClientId, StringComparer.Ordinal)
            || (audiences.Length > 1 && !string.Equals(String(claims, "azp"), settings.ClientId, StringComparison.Ordinal)))
        {
            throw Invalid("The ID token is meant for another application.");
        }

        var now = clock.GetUtcNow();
        if (Time(claims, "exp") is not { } expires || expires + s_skew < now || (Time(claims, "iat") is { } issued && issued - s_skew > now))
        {
            throw Invalid("The ID token is expired or not valid yet.");
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(String(claims, "nonce") ?? string.Empty), Encoding.UTF8.GetBytes(nonce)))
        {
            throw Invalid("The ID token was not issued for this sign-in.");
        }

        return claims;
    }

    private static bool Verify(JsonElement keys, string? keyId, string algorithm, byte[] signed, byte[] signature)
    {
        if (!keys.TryGetProperty("keys", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var key in list.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object || (keyId is not null && String(key, "kid") != keyId)
                || (String(key, "use") is { } use && use != "sig") || (String(key, "alg") is { } alg && alg != algorithm))
            {
                continue;
            }

            try
            {
                var hash = algorithm[2..] switch { "256" => HashAlgorithmName.SHA256, "384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
                if (algorithm[0] is 'R' or 'P' && String(key, "kty") == "RSA" && Bytes(key, "n") is { } n && Bytes(key, "e") is { } e)
                {
                    using var rsa = RSA.Create(new RSAParameters { Modulus = n, Exponent = e });
                    if (rsa.KeySize >= 2048 && rsa.VerifyData(signed, signature, hash, algorithm[0] == 'R' ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss))
                    {
                        return true;
                    }
                }
                else if (algorithm[0] == 'E' && String(key, "kty") == "EC" && Bytes(key, "x") is { } x && Bytes(key, "y") is { } y)
                {
                    var curve = (algorithm, String(key, "crv")) switch
                    {
                        ("ES256", "P-256") => ECCurve.NamedCurves.nistP256,
                        ("ES384", "P-384") => ECCurve.NamedCurves.nistP384,
                        ("ES512", "P-521") => ECCurve.NamedCurves.nistP521,
                        _ => (ECCurve?)null,
                    };
                    if (curve is { } named)
                    {
                        using var ec = ECDsa.Create(new ECParameters { Curve = named, Q = new ECPoint { X = x, Y = y } });
                        if (ec.VerifyData(signed, signature, hash))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (CryptographicException)
            {
                // A key that is not one: the next one, if any.
            }
        }

        return false;
    }

    private async Task<Discovery> DiscoverAsync(SsoSettings settings, CancellationToken cancellationToken)
    {
        var issuer = settings.Kind == SsoKind.Google ? GoogleIssuer : settings.Authority?.TrimEnd('/');
        if (issuer is null || !SsoProvider.IsAuthority(issuer))
        {
            throw new SsoException("sso.provider_unavailable", "The provider's issuer is not set.");
        }

        var now = clock.GetUtcNow();
        if (_discovery.TryGetValue(issuer, out var cached) && now - cached.At < s_cacheFor)
        {
            return cached.Document;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{issuer}/.well-known/openid-configuration");
        using var document = await SendAsync(request, "its discovery document", cancellationToken);
        var root = document.RootElement;
        // The issuer it names must be the one it was asked as: a document that names another is not this provider's.
        var named = String(root, "issuer")?.TrimEnd('/');
        var authorization = String(root, "authorization_endpoint");
        var token = String(root, "token_endpoint");
        var keys = String(root, "jwks_uri");
        if (!string.Equals(named, issuer, StringComparison.Ordinal) || !IsHttps(authorization) || !IsHttps(token) || !IsHttps(keys))
        {
            throw new SsoException("sso.provider_unavailable", "The provider's discovery document does not match its issuer, or names an address that is not https.");
        }

        var methods = root.TryGetProperty("token_endpoint_auth_methods_supported", out var listed) && listed.ValueKind == JsonValueKind.Array
            ? listed.EnumerateArray().Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() : null).ToList()
            : [];
        var discovery = new Discovery(String(root, "issuer")!, authorization!, token!, keys!, methods.Contains("client_secret_post"));
        _discovery[issuer] = (discovery, now);
        return discovery;
    }

    private async Task<JsonElement> KeysAsync(Discovery discovery, string? keyId, bool refresh, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_keys.TryGetValue(discovery.KeysEndpoint, out var cached)
            && (refresh ? now - cached.At < s_keysRefreshAfter : now - cached.At < s_cacheFor))
        {
            return cached.Keys;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, discovery.KeysEndpoint);
        using var document = await SendAsync(request, "its keys", cancellationToken);
        var keys = document.RootElement.Clone();
        _keys[discovery.KeysEndpoint] = (keys, now);
        return keys;
    }

    /// <summary>One request, its answer read as JSON up to <see cref="MaxResponseBytes"/>; a provider that refuses says so.</summary>
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Pulsar");
        // The client's timeout ends with the headers: the body is read within the same deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(s_deadline);
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new SsoException("sso.provider_unavailable", $"The provider's answer from {what} is too large.");
                }

                buffer.Write(chunk, 0, read);
            }

            if (!response.IsSuccessStatusCode)
            {
                // A refused code (expired, used, another client's) is the user's to retry; the rest the provider's.
                throw response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    ? new SsoException("sso.denied", $"The provider refused the sign-in at {what} ({(int)response.StatusCode}).")
                    : new SsoException("sso.provider_unavailable", $"The provider answered {(int)response.StatusCode} from {what}.");
            }

            buffer.Position = 0;
            var document = await JsonDocument.ParseAsync(buffer, cancellationToken: deadline.Token);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new SsoException("sso.provider_unavailable", $"The provider's answer from {what} is not a JSON object.");
            }

            return document;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new SsoException("sso.provider_unavailable", $"The provider could not be reached at {what}.", ex);
        }
    }

    private static bool IsHttps(string? address) =>
        address is { Length: <= 2048 } && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[]? Bytes(JsonElement key, string name)
    {
        try
        {
            return String(key, name) is { } value ? WebEncoders.Base64UrlDecode(value) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Time(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            && seconds is > 0 and < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>A name to show next to the link: one line, at most 64 characters.</summary>
    private static string? Name(string? value)
    {
        var line = value?.Trim();
        return string.IsNullOrEmpty(line) || line.Any(char.IsControl) ? null : line.Length > 64 ? line[..64] : line;
    }

    private static SsoException Invalid(string message, Exception? inner = null) => new("sso.token_invalid", message, inner);

    /// <param name="SecretInForm">The provider takes the client secret in the form (<c>client_secret_post</c>); otherwise HTTP Basic.</param>
    private sealed record Discovery(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string KeysEndpoint, bool SecretInForm);
}
