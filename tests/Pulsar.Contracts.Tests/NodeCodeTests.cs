using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Pulsar.Contracts.Tests;

/// <summary>
/// The node code (Phase 18): what Add node shows for PulsarAgentSetup.exe. The panel writes it, the agent's setup reads
/// it, so the format is exact and anything else is refused.
/// </summary>
public sealed class NodeCodeTests
{
    private const string Url = "https://panel.example.com:8443";
    private static readonly string s_token = Base64Url.EncodeToString(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string s_key = ProtocolCrypto.Fingerprint("panel key"u8);
    private static readonly string s_cert = NodeCode.CertificateHash("certificate"u8);

    [Fact]
    public void ACodeIsThePrefixAndTheBase64UrlOfItsJson()
    {
        var code = NodeCode.Create(Url, s_token, s_key, s_cert);

        Assert.StartsWith("pulsar-node-1.", code, StringComparison.Ordinal);
        Assert.DoesNotContain('=', code);
        Assert.Equal($$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":"{{s_cert}}"}""",
            Encoding.UTF8.GetString(Base64Url.DecodeFromChars(code.AsSpan(NodeCode.Prefix.Length))));
    }

    [Fact]
    public void ACodeReadsBackAsItWasMade()
    {
        Assert.True(NodeCode.TryParse(NodeCode.Create(Url, s_token, s_key, s_cert), out var parsed));

        Assert.Equal(Url, parsed.Url);
        Assert.Equal(s_token, parsed.Token);
        Assert.Equal(s_key, parsed.PanelKey);
        Assert.Equal(s_cert, parsed.CertificateSha256);
    }

    [Fact]
    public void ACodeWithoutTheCertificateSaysNull()
    {
        var code = NodeCode.Create(Url, s_token, s_key, null);

        Assert.EndsWith("\"cert\":null}", Encoding.UTF8.GetString(Base64Url.DecodeFromChars(code.AsSpan(NodeCode.Prefix.Length))), StringComparison.Ordinal);
        Assert.True(NodeCode.TryParse(code, out var parsed));
        Assert.Null(parsed.CertificateSha256);
    }

    [Fact]
    public void AReusableTokenFitsToo()
    {
        var token = NodeCode.ReusableTokenPrefix + s_token;

        Assert.True(NodeCode.TryParse(NodeCode.Create(Url, token, s_key, null), out var parsed));
        Assert.Equal(token, parsed.Token);
    }

    [Fact]
    public void TheCertificateHashIsTheLowercaseHexOfItsSha256()
    {
        byte[] der = [0x30, 0x03, 0x02, 0x01, 0x01];

        Assert.Equal(Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant(), NodeCode.CertificateHash(der));
    }

    [Fact]
    public void ItsTextNeverShowsTheToken()
    {
        Assert.True(NodeCode.TryParse(NodeCode.Create(Url, s_token, s_key, s_cert), out var parsed));

        var printed = parsed.ToString();

        Assert.DoesNotContain(s_token, printed, StringComparison.Ordinal);
        Assert.Contains("Token = ***", printed, StringComparison.Ordinal);
        Assert.Contains(Url, printed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://panel.example.com")]
    [InlineData("https://panel.example.com:8443")]
    [InlineData("https://192.0.2.10:8443")]
    [InlineData("https://[2001:db8::1]:8443")]
    [InlineData("https://localhost:8443")]
    [InlineData("http://localhost:5180")]
    [InlineData("http://127.0.0.1:5180")]
    [InlineData("http://[::1]:5180")]
    public void TheseAddressesAreTaken(string url) => Assert.True(NodeCode.IsValidUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData("panel.example.com")]
    [InlineData("/nodes")]
    [InlineData("http://panel.example.com")]
    [InlineData("http://192.0.2.10:5180")]
    [InlineData("http://loopback:5180")]
    [InlineData("http://localhost.example.com")]
    [InlineData("ftp://panel.example.com")]
    [InlineData("file:///C:/panel")]
    [InlineData("https://panel.example.com/")]
    [InlineData("https://panel.example.com/pulsar")]
    [InlineData("https://panel.example.com?a=1")]
    [InlineData("https://panel.example.com#top")]
    [InlineData("https://admin:secret@panel.example.com")]
    [InlineData("https://Panel.example.com")]
    [InlineData("HTTPS://panel.example.com")]
    [InlineData("https://panel.example.com:443")]
    [InlineData("https://panel.example.com ")]
    [InlineData(" https://panel.example.com")]
    [InlineData("https://pänel.example.com")]
    [InlineData("https://panel.example.com\n")]
    public void TheseAddressesAreNot(string url)
    {
        Assert.False(NodeCode.IsValidUrl(url));
        Assert.Throws<ArgumentException>(() => NodeCode.Create(url, s_token, s_key, null));
    }

    [Fact]
    public void AnAddressLongerThanTheLimitIsNot()
    {
        // Labels of 50 letters, the last one shortened: an address Uri itself takes at either length.
        static string Address(int length)
        {
            var host = string.Join('.', Enumerable.Repeat(new string('a', 50), 11));
            return "https://" + host[..(length - "https://".Length)];
        }

        Assert.True(NodeCode.IsValidUrl(Address(NodeCode.MaxUrlLength)));
        Assert.True(Uri.TryCreate(Address(NodeCode.MaxUrlLength + 1), UriKind.Absolute, out _));
        Assert.False(NodeCode.IsValidUrl(Address(NodeCode.MaxUrlLength + 1)));
        Assert.True(NodeCode.TryParse(NodeCode.Create(Address(NodeCode.MaxUrlLength), NodeCode.ReusableTokenPrefix + s_token, s_key, s_cert), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("pulsar_enroll_")]
    [InlineData("pulsar_enroll_short")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA ")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHy+")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyB")]
    [InlineData("PULSAR_ENROLL_AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    public void AMalformedTokenIsRefused(string token)
    {
        Assert.Throws<ArgumentException>(() => NodeCode.Create(Url, token, s_key, null));
        Assert.False(NodeCode.TryParse(Code(Json(token: token)), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:abc")]
    [InlineData("SHA256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("sha512:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("sha256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=")]
    public void AMalformedPanelKeyIsRefused(string key)
    {
        Assert.Throws<ArgumentException>(() => NodeCode.Create(Url, s_token, key, null));
        Assert.False(NodeCode.TryParse(Code(Json(key: key)), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef")]
    public void AMalformedCertificateHashIsRefused(string cert)
    {
        Assert.Throws<ArgumentException>(() => NodeCode.Create(Url, s_token, s_key, cert));
        Assert.False(NodeCode.TryParse(Code(Json(cert: $"\"{cert}\"")), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pulsar-node-1.")]
    [InlineData("pulsar-node-1")]
    [InlineData("eyJ1cmwiOiJodHRwczovL3BhbmVsIn0")]
    public void ThisIsNoCode(string? code) => Assert.False(NodeCode.TryParse(code, out _));

    [Theory]
    [InlineData("pulsar-node-2.")]
    [InlineData("Pulsar-node-1.")]
    [InlineData("pulsar-node-1:")]
    [InlineData(" pulsar-node-1.")]
    [InlineData("pulsar-node-1. ")]
    public void AnotherPrefixIsRefused(string prefix)
    {
        var valid = NodeCode.Create(Url, s_token, s_key, s_cert);

        Assert.False(NodeCode.TryParse(prefix + valid[NodeCode.Prefix.Length..], out _));
    }

    [Fact]
    public void WhitespaceAnywhereIsRefused()
    {
        var valid = NodeCode.Create(Url, s_token, s_key, s_cert);

        Assert.False(NodeCode.TryParse(valid + " ", out _));
        Assert.False(NodeCode.TryParse(valid + "\n", out _));
        Assert.False(NodeCode.TryParse(" " + valid, out _));
        Assert.False(NodeCode.TryParse(valid[..40] + " " + valid[40..], out _));
        Assert.False(NodeCode.TryParse(valid[..40] + "\r\n" + valid[40..], out _));
    }

    [Fact]
    public void PaddingAndTheStandardAlphabetAreRefused()
    {
        // A length that base64 pads.
        var json = Json();
        while (Encoding.UTF8.GetByteCount(json) % 3 == 0)
        {
            json = json[..^1] + " }";
        }

        var code = Code(json);
        var encoded = code[NodeCode.Prefix.Length..];
        Assert.True(NodeCode.TryParse(code, out _));

        Assert.False(NodeCode.TryParse(code + new string('=', 4 - (encoded.Length % 4)), out _));
        Assert.False(NodeCode.TryParse(code[..30] + "+" + code[31..], out _));
        Assert.False(NodeCode.TryParse(code[..30] + "/" + code[31..], out _));
        Assert.False(NodeCode.TryParse(code[..30] + "." + code[31..], out _));
    }

    [Fact]
    public void ALengthNoBase64HasIsRefused()
    {
        var code = Code(Json());
        var encoded = code[NodeCode.Prefix.Length..];
        var cut = code[..^((encoded.Length % 4) + 3)];

        Assert.Equal(1, (cut.Length - NodeCode.Prefix.Length) % 4);
        Assert.False(NodeCode.TryParse(cut, out _));
    }

    [Fact]
    public void OnlyTheCanonicalSpellingIsTaken()
    {
        // A length whose last character carries unused bits: setting one of them spells the same bytes another way.
        var json = Json();
        while (Encoding.UTF8.GetByteCount(json) % 3 != 1)
        {
            json = json[..^1] + " }";
        }

        var code = Code(json);
        Assert.True(NodeCode.TryParse(code, out _));
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var other = code[..^1] + alphabet[alphabet.IndexOf(code[^1], StringComparison.Ordinal) + 1];

        Assert.Equal(Convert.FromBase64String(Standard(code)), Convert.FromBase64String(Standard(other)));
        Assert.False(NodeCode.TryParse(other, out _));

        static string Standard(string code)
        {
            var encoded = code[NodeCode.Prefix.Length..].Replace('-', '+').Replace('_', '/');
            return encoded + new string('=', (4 - (encoded.Length % 4)) % 4);
        }
    }

    [Fact]
    public void ACodeLongerThanTheLimitIsRefusedBeforeItIsRead()
    {
        var oversized = Code(Json() + new string(' ', NodeCode.MaxLength));

        Assert.True(oversized.Length > NodeCode.MaxLength);
        Assert.False(NodeCode.TryParse(oversized, out _));
        Assert.False(NodeCode.TryParse(NodeCode.Prefix + new string('A', 1_000_000), out _));
    }

    [Fact]
    public void ACodeAtTheLimitIsStillRead()
    {
        // JSON allows spaces between its tokens: they fill the code up to its limit.
        var json = Json();
        while (Code(json[..^1] + " }").Length <= NodeCode.MaxLength)
        {
            json = json[..^1] + " }";
        }

        Assert.True(Code(json).Length <= NodeCode.MaxLength);
        Assert.True(NodeCode.TryParse(Code(json), out _));
    }

    public static TheoryData<string> NotTheFourMembers =>
    [
        "[]",
        "\"pulsar\"",
        "null",
        "42",
        "{}",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}"}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":null,"extra":1}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":null,"url":"{{Url}}"}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","token":"{{s_token}}","cert":null}""",
        $$"""{"URL":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":null}""",
        $$"""{"url":42,"token":"{{s_token}}","key":"{{s_key}}","cert":null}""",
        $$"""{"url":null,"token":"{{s_token}}","key":"{{s_key}}","cert":null}""",
        $$"""{"url":"{{Url}}","token":null,"key":"{{s_key}}","cert":null}""",
        $$"""{"url":"{{Url}}","token":["{{s_token}}"],"key":"{{s_key}}","cert":null}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":{"id":"{{s_key}}"},"cert":null}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":false}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":0}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":null,}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":null /* c */}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":null}{}""",
        $$"""{"url":"{{Url}}","token":"{{s_token}}","key":"{{s_key}}","cert":null""",
        $$"""{"url":"{{Url}}","token":"\ud800","key":"{{s_key}}","cert":null}""",
        $$"""{"url":"{{Url}}\u0000","token":"{{s_token}}","key":"{{s_key}}","cert":null}""",
        $$"""{"url":"http://panel.example.com","token":"{{s_token}}","key":"{{s_key}}","cert":null}""",
    ];

    [Theory]
    [MemberData(nameof(NotTheFourMembers))]
    public void JsonOtherThanTheFourMembersIsRefused(string json) => Assert.False(NodeCode.TryParse(Code(json), out _));

    [Fact]
    public void TheMembersMayComeInAnyOrderAndEscaped()
    {
        var json = $$"""{"cert":null,"key":"{{s_key}}","\u0074oken":"{{s_token}}","url":"https:\/\/panel.example.com:8443"}""";

        Assert.True(NodeCode.TryParse(Code(json), out var parsed));
        Assert.Equal(Url, parsed.Url);
        Assert.Equal(s_token, parsed.Token);
    }

    [Fact]
    public void BytesThatAreNotUtf8AreRefused()
    {
        var json = Encoding.UTF8.GetBytes(Json());
        json[9] = 0xFF;

        Assert.False(NodeCode.TryParse(NodeCode.Prefix + Base64Url.EncodeToString(json), out _));
        Assert.False(NodeCode.TryParse(NodeCode.Prefix + Base64Url.EncodeToString([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Json())]), out _));
    }

    [Fact]
    public void DeepJsonIsRefused()
    {
        var deep = new string('[', 100) + new string(']', 100);

        Assert.False(NodeCode.TryParse(Code(deep), out _));
        Assert.False(NodeCode.TryParse(Code($$"""{"url":{{deep}},"token":"{{s_token}}","key":"{{s_key}}","cert":null}"""), out _));
    }

    private static string Json(string? url = null, string? token = null, string? key = null, string? cert = null) =>
        $$"""{"url":"{{url ?? Url}}","token":"{{token ?? s_token}}","key":"{{key ?? s_key}}","cert":{{cert ?? $"\"{s_cert}\""}}}""";

    private static string Code(string json) => NodeCode.Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
