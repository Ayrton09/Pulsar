using System.Security.Cryptography;
using System.Text.Json;

namespace Pulsar.Contracts.Tests;

public sealed class ProtocolCryptoTests
{
    [Fact]
    public void SignedDataIsUnambiguous()
    {
        var ab_c = ProtocolCrypto.SignedData("p", ProtocolCrypto.Utf8("ab"), ProtocolCrypto.Utf8("c"));
        var a_bc = ProtocolCrypto.SignedData("p", ProtocolCrypto.Utf8("a"), ProtocolCrypto.Utf8("bc"));

        Assert.NotEqual(ab_c, a_bc);
        Assert.NotEqual(ProtocolCrypto.SignedData("p1", [1]), ProtocolCrypto.SignedData("p2", [1]));
    }

    [Fact]
    public void SignedMessagesVerifyOnlyForTheirPurposeKeyAndPayload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var message = ProtocolCrypto.SignMessage(key, "k1", AgentProtocol.Purposes.Command, [1, 2, 3]);

        Assert.Equal(ProtocolCrypto.SignatureLength, message.Signature.Length);
        Assert.True(ProtocolCrypto.VerifyMessage(message, AgentProtocol.Purposes.Command, publicKey));
        Assert.False(ProtocolCrypto.VerifyMessage(message, AgentProtocol.Purposes.EnrollmentResult, publicKey));
        Assert.False(ProtocolCrypto.VerifyMessage(message, AgentProtocol.Purposes.Command, other.ExportSubjectPublicKeyInfo()));
        Assert.False(ProtocolCrypto.VerifyMessage(message with { Payload = [1, 2, 4] }, AgentProtocol.Purposes.Command, publicKey));
    }

    [Fact]
    public void MalformedOrForeignKeysNeverVerify()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var data = ProtocolCrypto.SignedData("p", [1]);
        var signature = new byte[ProtocolCrypto.SignatureLength];

        Assert.False(ProtocolCrypto.Verify(p384.ExportSubjectPublicKeyInfo(), data, signature));
        Assert.False(ProtocolCrypto.Verify([1, 2, 3], data, signature));
        Assert.Throws<CryptographicException>(() => ProtocolCrypto.ImportPublicKey(p384.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void APointOffTheCurveIsAMalformedKey()
    {
        // Windows refuses it as an unsupported curve (PlatformNotSupportedException), which callers that catch
        // CryptographicException would let escape: an enrollment or a rotation would fail with a 500 instead of a refusal.
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offCurve = OffCurve(p256.ExportSubjectPublicKeyInfo());
        var data = ProtocolCrypto.SignedData("p", [1]);

        // A CryptographicException or one derived from it (OpenSSL's, on Linux): what callers catch.
        Assert.ThrowsAny<CryptographicException>(() => ProtocolCrypto.ImportPublicKey(offCurve));
        Assert.False(ProtocolCrypto.Verify(offCurve, data, ProtocolCrypto.Sign(p256, data)));
    }

    [Fact]
    public void AKeyIsItsEncodingAndNothingAfter()
    {
        // Kept as sent, a byte after the key would give it a fingerprint of its own that the key itself does not have.
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] trailing = [.. p256.ExportSubjectPublicKeyInfo(), 0];

        Assert.Throws<CryptographicException>(() => ProtocolCrypto.ImportPublicKey(trailing));
        Assert.Throws<CryptographicException>(() => ProtocolCrypto.ImportPublicKey([]));
    }

    /// <summary>The key with one byte of its point's Y coordinate changed: the same encoding, a point not on P-256.</summary>
    internal static byte[] OffCurve(byte[] subjectPublicKeyInfo)
    {
        var changed = subjectPublicKeyInfo.ToArray();
        changed[^2] ^= 0x5a;
        return changed;
    }

    [Fact]
    public void FingerprintsAreStableAndUrlSafe()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();

        var fingerprint = ProtocolCrypto.Fingerprint(spki);

        Assert.Equal(fingerprint, ProtocolCrypto.Fingerprint(spki));
        Assert.Matches("^sha256:[A-Za-z0-9_-]{43}$", fingerprint);
    }

    [Fact]
    public void CommandsRoundTripWithTheirDiscriminator()
    {
        var envelope = new CommandEnvelope(1, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 7,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), null, null, new PingCommand("hi"));

        var json = ProtocolJson.Serialize(envelope);
        var back = ProtocolJson.Deserialize<CommandEnvelope>(json);

        Assert.Contains("\"$type\":\"node.ping\"", System.Text.Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.Equal(new PingCommand("hi"), back.Command);
        Assert.Equal(envelope with { Command = back.Command }, back);
    }

    [Fact]
    public void UnknownCommandsFailToDeserialize()
    {
        var json = """{"protocolVersion":1,"commandId":"0192f1a0-0000-7000-8000-000000000001","nodeId":"0192f1a0-0000-7000-8000-000000000002","sessionId":"0192f1a0-0000-7000-8000-000000000003","sequence":1,"issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","command":{"$type":"node.format_disk"}}"""u8.ToArray();

        Assert.ThrowsAny<JsonException>(() => ProtocolJson.Deserialize<CommandEnvelope>(json));
    }
}
