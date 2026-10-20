using System.Security.Cryptography;
using System.Text;
using Pulsar.Contracts;

namespace Pulsar.Contracts.Tests;

/// <summary>A release's manifest is taken only signed by a trusted key, for its purpose, and well formed.</summary>
public sealed class UpdateVerifierTests
{
    private const string Zip = "pulsar-1.2.0-windows-x64.zip";

    [Fact]
    public void ASignedManifestIsTakenWithItsFiles()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();

        var check = Verifier(key).Verify(manifest, Sign(key, manifest));

        Assert.Null(check.ErrorCode);
        Assert.Equal("1.2.0", check.Manifest!.Version);
        Assert.Equal(ProtocolCrypto.Fingerprint(key.ExportSubjectPublicKeyInfo()), check.KeyId);
        Assert.Equal(new string('a', 64), check.Manifest.Find(Zip)!.Sha256);
    }

    [Fact]
    public void AnythingElseIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        var verifier = Verifier(key);

        // Another key, even a well-formed one, is not trusted.
        Assert.Equal("update.unknown_key", verifier.Verify(manifest, Sign(other, manifest)).ErrorCode);
        // One byte changed after signing.
        var changed = manifest.ToArray();
        changed[^3] ^= 1;
        Assert.Equal("update.bad_signature", verifier.Verify(changed, Sign(key, manifest)).ErrorCode);
        // The right key, but a signature made for something else.
        var elsewhere = ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData("panel-command", manifest));
        Assert.Equal("update.bad_signature", verifier.Verify(manifest, SignatureFile(key, elsewhere)).ErrorCode);
        // No signature, or not one.
        Assert.Equal("update.unsigned", verifier.Verify(manifest, "{}"u8).ErrorCode);
        Assert.Equal("update.unsigned", verifier.Verify(manifest, "not json"u8).ErrorCode);
        Assert.Equal("update.invalid_manifest", verifier.Verify(manifest, []).ErrorCode);
        Assert.Equal("update.invalid_manifest", verifier.Verify(new byte[UpdateRelease.MaxManifestBytes + 1], Sign(key, manifest)).ErrorCode);
        // A build without keys takes nothing.
        Assert.False(new UpdateVerifier([]).HasKeys);
        Assert.Equal("update.unknown_key", new UpdateVerifier(["not base64", ""]).Verify(manifest, Sign(key, manifest)).ErrorCode);
    }

    [Theory]
    [InlineData("""{"product":"other","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[F]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[F]}""")]
    [InlineData("""{"product":"pulsar","version":"01.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[F]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.3.0","published":"2026-10-20T00:00:00Z","files":[F]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","files":[F]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[F,F]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[{"name":"other.zip","size":10,"sha256":"HASH"}]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[F,{"name":"..\\evil.exe","size":10,"sha256":"HASH"}]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[F,{"name":".hidden","size":10,"sha256":"HASH"}]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[{"name":"pulsar-1.2.0-windows-x64.zip","size":0,"sha256":"HASH"}]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[{"name":"pulsar-1.2.0-windows-x64.zip","size":10,"sha256":"ABC"}]}""")]
    [InlineData("""{"product":"pulsar","version":"1.2.0","minimumUpdateFrom":"1.0.0","published":"2026-10-20T00:00:00Z","files":[F,{"name":"pulsar-update.json","size":10,"sha256":"HASH"}]}""")]
    public void ASignedButMalformedManifestIsRefused(string json)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Encoding.UTF8.GetBytes(json
            .Replace("F", $$"""{"name":"{{Zip}}","size":10,"sha256":"{{new string('a', 64)}}"}""", StringComparison.Ordinal)
            .Replace("HASH", new string('b', 64), StringComparison.Ordinal));

        Assert.Null(UpdateVerifier.Read(manifest));
        Assert.Equal("update.invalid_manifest", Verifier(key).Verify(manifest, Sign(key, manifest)).ErrorCode);
    }

    [Fact]
    public void NotesAreBounded()
    {
        Assert.NotNull(UpdateVerifier.Read(Manifest(new string('n', UpdateRelease.MaxNotesLength))));
        Assert.Null(UpdateVerifier.Read(Manifest(new string('n', UpdateRelease.MaxNotesLength + 1))));
    }

    [Fact]
    public void VersionsCompareAsReleases()
    {
        Assert.True(UpdateVersion.TryParse("1.10.0", out var ten));
        Assert.True(UpdateVersion.TryParse("1.9.3", out var nine));
        Assert.True(ten > nine);
        Assert.False(UpdateVersion.TryParse("1.2.3-beta", out _));
        Assert.False(UpdateVersion.TryParse(" 1.2.3", out _));
        Assert.False(UpdateVersion.TryParse("1.2.٣", out _));

        Assert.True(UpdateVersion.TryParse("1.0.0", out var one));
        Assert.Equal((one, false), UpdateVersion.Installed("1.0.0+abc123"));
        Assert.Equal((one, true), UpdateVersion.Installed("1.0.0-dev"));
        Assert.Null(UpdateVersion.Installed("not a version"));
        Assert.True(one.IsNewerThan(UpdateVersion.Installed("1.0.0-dev")!.Value));
        Assert.False(one.IsNewerThan(UpdateVersion.Installed("1.0.0")!.Value));
        Assert.False(one.IsNewerThan(UpdateVersion.Installed("1.0.1")!.Value));
    }

    [Fact]
    public void DownloadsGoOnlyToGitHubOverHttps()
    {
        Assert.True(UpdateVersion.TryParse("1.2.0", out var version));
        Assert.Equal("https://github.com/Ayrton09/Pulsar/releases/download/v1.2.0/pulsar-1.2.0-windows-x64.zip",
            UpdateRelease.File(version, UpdateRelease.ZipName(version)).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => UpdateRelease.File(version, "../x"));
        Assert.True(UpdateRelease.IsAllowedDownload(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x")));
        Assert.False(UpdateRelease.IsAllowedDownload(new Uri("http://github.com/Ayrton09/Pulsar")));
        Assert.False(UpdateRelease.IsAllowedDownload(new Uri("https://github.com:8443/x")));
        Assert.False(UpdateRelease.IsAllowedDownload(new Uri("https://github.com.example.com/x")));
        Assert.False(UpdateRelease.IsAllowedDownload(new Uri("https://user@github.com/x")));
        Assert.False(UpdateRelease.IsAllowedDownload(new Uri("https://example.com/x")));
    }

    private static UpdateVerifier Verifier(ECDsa key) => new([Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())]);

    private static byte[] Manifest(string? notes = "What changed.") =>
        ProtocolJson.Serialize(new UpdateManifest("pulsar", "1.2.0", "1.0.0", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), notes,
            [new UpdateFile(Zip, 1234, new string('a', 64)), new UpdateFile("PulsarSetup-1.2.0.exe", 99, new string('c', 64))]));

    private static byte[] Sign(ECDsa key, byte[] manifest) =>
        SignatureFile(key, ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(UpdateRelease.SignaturePurpose, manifest)));

    private static byte[] SignatureFile(ECDsa key, byte[] signature) =>
        ProtocolJson.Serialize(new UpdateSignature(ProtocolCrypto.Fingerprint(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(signature)));
}
