using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pulsar.Contracts;

/// <summary>
/// The node code (Phase 18, docs/architecture/03 "Node code"): what <c>PulsarAgentSetup.exe</c> needs to enroll a machine,
/// in one string an administrator copies from <b>Add node</b>. <see cref="Prefix"/> followed by the base64url (no padding)
/// of UTF-8 JSON <c>{"url":…,"token":…,"key":…,"cert":…}</c>: the panel's origin, the enrollment token, the fingerprint
/// of the panel key the agent pins, and the lowercase hex SHA-256 of the panel's TLS certificate (its DER), or null when
/// the panel cannot tell which certificate its clients see. It carries the token, so it is as secret as the token, for as
/// long as the token works: shown once, never stored, never logged (<see cref="ToString"/> leaves the token out).
/// </summary>
public sealed class NodeCode
{
    /// <summary>The code's format and version: a later format takes another prefix.</summary>
    public const string Prefix = "pulsar-node-1.";

    /// <summary>The longest code taken, checked before anything is decoded.</summary>
    public const int MaxLength = 2048;

    /// <summary>The longest panel origin a code carries.</summary>
    public const int MaxUrlLength = 512;

    /// <summary>What starts a reusable enrollment token (Phase 14); a single-use token is the random part alone.</summary>
    public const string ReusableTokenPrefix = "pulsar_enroll_";

    private const string KeyPrefix = "sha256:";

    // 32 random bytes (tokens) or a SHA-256 (key fingerprints) in base64url without padding.
    private const int RandomPartLength = 43;

    private static readonly SearchValues<char> s_base64Url =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static readonly SearchValues<char> s_lowerHex = SearchValues.Create("0123456789abcdef");

    private NodeCode(string url, string token, string panelKey, string? certificateSha256)
    {
        Url = url;
        Token = token;
        PanelKey = panelKey;
        CertificateSha256 = certificateSha256;
    }

    /// <summary>The panel's origin (<c>https://panel.example.com:8443</c>, no trailing slash): JSON <c>url</c>.</summary>
    public string Url { get; }

    /// <summary>The enrollment token (a secret): JSON <c>token</c>.</summary>
    public string Token { get; }

    /// <summary>The panel key's fingerprint (<c>sha256:…</c>, <see cref="ProtocolCrypto.Fingerprint"/>): JSON <c>key</c>.</summary>
    public string PanelKey { get; }

    /// <summary>
    /// The lowercase hex SHA-256 of the DER of the panel's TLS leaf certificate (<see cref="CertificateHash"/>): JSON
    /// <c>cert</c>. Null when the panel does not know it (behind a proxy, plain HTTP): the setup then needs a certificate
    /// the machine already trusts.
    /// </summary>
    public string? CertificateSha256 { get; }

    /// <summary>A certificate's hash as a code carries it: the lowercase hex SHA-256 of its DER encoding.</summary>
    public static string CertificateHash(ReadOnlySpan<byte> certificateDer) => Convert.ToHexStringLower(SHA256.HashData(certificateDer));

    /// <summary>The code for these values; throws <see cref="ArgumentException"/> when one is not as <see cref="TryParse"/> requires.</summary>
    public static string Create(string url, string token, string panelKey, string? certificateSha256)
    {
        if (!IsValidUrl(url))
        {
            throw new ArgumentException("The panel's address must be an https origin (http only on this machine), without a trailing slash.", nameof(url));
        }

        if (!IsToken(token))
        {
            throw new ArgumentException("Not an enrollment token.", nameof(token));
        }

        if (!IsPanelKey(panelKey))
        {
            throw new ArgumentException("Not a panel key fingerprint (sha256:…).", nameof(panelKey));
        }

        if (certificateSha256 is not null && !IsCertificateHash(certificateSha256))
        {
            throw new ArgumentException("Not a lowercase hex SHA-256.", nameof(certificateSha256));
        }

        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteString("url", url);
            writer.WriteString("token", token);
            writer.WriteString("key", panelKey);
            writer.WriteString("cert", certificateSha256);
            writer.WriteEndObject();
        }

        var code = Prefix + Base64Url.EncodeToString(json.WrittenSpan);
        return code.Length <= MaxLength ? code : throw new ArgumentException("The code would be too long.", nameof(url));
    }

    /// <summary>
    /// Reads a code, strictly: at most <see cref="MaxLength"/> characters, exactly <see cref="Prefix"/>, then base64url
    /// without padding or whitespace in its one canonical form, of a UTF-8 JSON object with exactly the members <c>url</c>,
    /// <c>token</c>, <c>key</c> (strings) and <c>cert</c> (a string or null), each once, and each well formed:
    /// <see cref="IsValidUrl"/>, a single-use or reusable enrollment token, a <c>sha256:</c> fingerprint, 64 lowercase hex
    /// digits. Anything else is not a code. Callers trim what a person pasted; this does not.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? code, [NotNullWhen(true)] out NodeCode? nodeCode)
    {
        nodeCode = null;
        if (code is null || code.Length > MaxLength || !code.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryDecode(code.AsSpan(Prefix.Length)) is not { Length: > 0 } json)
        {
            return false;
        }

        string? url = null, token = null, key = null, cert = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(member.Name))
                {
                    return false;
                }

                var value = member.Value;
                switch (member.Name)
                {
                    case "url" when value.ValueKind == JsonValueKind.String:
                        url = value.GetString();
                        break;
                    case "token" when value.ValueKind == JsonValueKind.String:
                        token = value.GetString();
                        break;
                    case "key" when value.ValueKind == JsonValueKind.String:
                        key = value.GetString();
                        break;
                    case "cert" when value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                        cert = value.GetString();
                        break;
                    default:
                        return false;
                }
            }

            if (seen.Count != 4)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }

        if (!IsValidUrl(url) || !IsToken(token) || !IsPanelKey(key) || (cert is not null && !IsCertificateHash(cert)))
        {
            return false;
        }

        nodeCode = new NodeCode(url, token, key, cert);
        return true;
    }

    /// <summary>
    /// A panel address a code may carry: an origin (<c>scheme://host[:port]</c>, in lower case, without a trailing slash,
    /// path, query, fragment or credentials, and no default port) in printable ASCII, at most <see cref="MaxUrlLength"/>
    /// characters, over https; plain http only for <c>localhost</c> or a loopback address, as the agent accepts it.
    /// </summary>
    public static bool IsValidUrl([NotNullWhen(true)] string? url)
    {
        if (url is null || url.Length is 0 or > MaxUrlLength || url.AsSpan().ContainsAnyExceptInRange('!', '~')
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant(), url, StringComparison.Ordinal))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri));
    }

    /// <summary>The code without its token, for logs and debuggers.</summary>
    public override string ToString() =>
        $"NodeCode {{ Url = {Url}, Token = ***, PanelKey = {PanelKey}, CertificateSha256 = {CertificateSha256 ?? "null"} }}";

    // "localhost" or a loopback address, and nothing a name server answers for ("loopback", which Uri also counts).
    private static bool IsLoopback(Uri uri) =>
        uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address)
            : string.Equals(uri.Host, "localhost", StringComparison.Ordinal);

    private static bool IsToken([NotNullWhen(true)] string? token) =>
        token is not null && IsRandomPart(token.StartsWith(ReusableTokenPrefix, StringComparison.Ordinal) ? token.AsSpan(ReusableTokenPrefix.Length) : token);

    private static bool IsPanelKey([NotNullWhen(true)] string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) && IsRandomPart(key.AsSpan(KeyPrefix.Length));

    private static bool IsCertificateHash(string hash) => hash.Length == 64 && !hash.AsSpan().ContainsAnyExcept(s_lowerHex);

    // 32 bytes in base64url without padding, in its canonical spelling.
    private static bool IsRandomPart(ReadOnlySpan<char> value) => value.Length == RandomPartLength && TryDecode(value) is { Length: 32 };

    /// <summary>
    /// Base64url without padding or whitespace (which the decoder would skip), in its one spelling: the bits past the last
    /// byte are zero. Null otherwise.
    /// </summary>
    private static byte[]? TryDecode(ReadOnlySpan<char> encoded)
    {
        if (encoded.ContainsAnyExcept(s_base64Url) || encoded.Length % 4 == 1)
        {
            return null;
        }

        byte[] decoded;
        try
        {
            // It throws, rather than say no, at bits set past the last byte.
            decoded = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        return encoded.SequenceEqual(Base64Url.EncodeToString(decoded)) ? decoded : null;
    }
}
