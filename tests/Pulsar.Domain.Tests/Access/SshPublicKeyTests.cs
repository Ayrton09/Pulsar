using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Security;
using Pulsar.Domain.Access;
using Ed25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace Pulsar.Domain.Tests.Access;

/// <summary>
/// SSH public keys as users paste them (docs/guides/sftp.md, "SSH keys"): what ssh-keygen and PuTTYgen give is read
/// with OpenSSH's fingerprint, keys made here of every accepted type too, and anything malformed, oversized, of another
/// type than it says, weak or of a refused kind is refused with its reason.
/// </summary>
public sealed class SshPublicKeyTests
{
    // Made by ssh-keygen (OpenSSH for Windows 9.5), with the fingerprints and sizes `ssh-keygen -lf` printed for them.
    public static TheoryData<string, string, int, string> OpenSshKeys => new()
    {
        {
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF test ed25519@pulsar",
            SshPublicKey.Ed25519, 256, "SHA256:vkk7w82HhKLd8llWk4tSM08y8S4OwpU/+lEv9zJ9m8s"
        },
        {
            "ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBNPDR3ovAfbPSBBZFjyqK3EFFqqIybIaykXUxDHz1Vl/6XS7CvO0IzrWBIwOI6wMxsnHiMJHrAu+KjVcmQWs4F8= test ecdsa256@pulsar",
            SshPublicKey.EcdsaP256, 256, "SHA256:mXWBiO8cEbFiOn37kiZb8JJqjBeJHQWpaLBD1Q+1m40"
        },
        {
            "ecdsa-sha2-nistp384 AAAAE2VjZHNhLXNoYTItbmlzdHAzODQAAAAIbmlzdHAzODQAAABhBFou9EsC/gQDP8/W3lsdC0U2Xr1ih6X6p1AV35vBPfuYlzJP/pGbOOjIcyizBE8NYNrYtFygnsfV7RMFzWn4tsZ1MtLLMhEcJ/ccVWj5s7nAuHbWR7AXv5K9swmU849ddg== test ecdsa384@pulsar",
            SshPublicKey.EcdsaP384, 384, "SHA256:Fg0jFaCxkWnTkZ0tSDn8IcQBvmgT1HDkFMR1xxtFdCY"
        },
        {
            "ecdsa-sha2-nistp521 AAAAE2VjZHNhLXNoYTItbmlzdHA1MjEAAAAIbmlzdHA1MjEAAACFBAAZ4vWPB0d7SIntwSjY7SuAl9Cck0GqVuR5foyPSS3GoJF7GAUykxbZIG2GAfwvfNXdTz6VQK8vHQyOQvbsATwO5gApHHctiCWgfQhBbKyJnTxxW6laHVSGjw1hV1JnCSUlbHKnQmOFJpJ+PcgLPjzVUvRHV/ergnBksHHzPaRA13JwRQ== test ecdsa521@pulsar",
            SshPublicKey.EcdsaP521, 521, "SHA256:kowe4lKtjRJvBHBAQnAWSSoIXfp3BFx91tWweyVWCT0"
        },
        {
            "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQD1k34FpTcdfCpYmoFpXhqT6VowBpb0pYe19+cBNuJ/HOllts3FIzJe9XAbj5zskBSTyyrqnBegE0kcakpoHOxgy+VbLEJqsPOn7K+47okUc0a7VRNvyS5PoMe/SUJPh2rSqVbcETajRXN6lh+b1nZtiuH8OjlZHW1PTxsbgi137Nyfd1coHunwFEu7sZxKN9jVY/KyyZEz+Mu4zfxl0xyNPRp/HY9PO63t9Bw9SX8x9oQgW5yqI5QKXmr5Zg807qgA2WPG2Gh35xoy5ps1nivEMzg++elArTSFVXuxZiDqXM5QYwsdPoIi00iClokFEfBXTy3Nck6IRHzxpUHJUUrL test rsa2048@pulsar",
            SshPublicKey.Rsa, 2048, "SHA256:/0/L/8Hdmz9APW9fv67RNNEmDirPJ7gxm/SsSUGaOEs"
        },
    };

    private const string Rsa1024 =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAAAgQDQqtVpzIHqQGowuAJdDkyQhbIsnDsCis40igd5A0Rx+yIpsR1fJNIUm0KWEmPuMnL10CsHtFL4MOvHWeDqEP2ZgMtPIoRLw88d7IpMAzuGp2jCd3GXOOuWiJoDMkD7kXkIGu3U89HL+/PD6xXss1tjbn3wgsVOjiiHUI+kECGsIQ== test rsa1024@pulsar";

    private const string Dsa =
        "ssh-dss AAAAB3NzaC1kc3MAAACBAKKMW3h4X1RGEy4R1vFF/rZvXgn/Lhvk7+zet9ry5afund9b7/dyUKIJSon7gSzgLzvQAmelXK9nvZ55t7RGG+gcDmEwZvFDR0oMT4so+C324p5W4R2Z1uZF75EuHehlB2m+YKZAeyLPcsDkvLmn1UgnMB8XHrlRzqbsE+CldlZrAAAAFQDpLiJ2s3mf5c/c9J1RHe89OmbiAwAAAIBkTXhf7RvZ6PnjgYh3qDBzyKcqMWaG4VvAKDDDFoIzsLpjIAISN0Al7vHShzYBaGZ8Uf/hg1dJaRnNC6gv6kVBWPwBcDJsrJL19cwIC9DtxJQStdX7xKtt30DTfG2gnGQqxorHxO8iIVOT2M5uObeZTx96HKMDnXmv4QUibsVObwAAAIBMwGV1pCOfEwjMrxdSo8A07m3F3QXucOJv3s01L07dR+wMI/mLSBUfyTq/UTX48rX/bcdoXNJ1MlVD53b/EPXbSGVwINN2PSJ66EVAU253vfkuyZfAxKCE14PqscTo4MKUEMB7/mR1vV+F+6brWVt0Mbu8lxTzZzj/2ly4vPkB6g== test dsa@pulsar";

    [Theory]
    [MemberData(nameof(OpenSshKeys))]
    public void KeysFromSshKeygenAreReadWithOpenSshsFingerprint(string line, string type, int bits, string fingerprint)
    {
        var key = SshPublicKey.Parse(line).Value;

        Assert.Equal(type, key.Type);
        Assert.Equal(bits, key.Bits);
        Assert.Equal(fingerprint, key.Fingerprint);
        Assert.StartsWith("test ", key.Comment, StringComparison.Ordinal);
        Assert.Equal(line[..line.LastIndexOf(" test ", StringComparison.Ordinal)], key.ToOpenSsh());

        // The same key, presented as a blob at sign-in.
        var presented = SshPublicKey.FromBlob(key.Blob).Value;
        Assert.Equal((key.Type, key.Bits, key.Fingerprint, (string?)null), (presented.Type, presented.Bits, presented.Fingerprint, presented.Comment));
    }

    [Fact]
    public void KeysOfEveryAcceptedTypeMadeHereAreRead()
    {
        foreach (var (blob, type, bits) in new[]
                 {
                     (SshTestKeys.Ed25519Blob(SshTestKeys.NewEd25519().PublicKey), SshPublicKey.Ed25519, 256),
                     (SshTestKeys.EcdsaBlob(ECDsa.Create(ECCurve.NamedCurves.nistP256)), SshPublicKey.EcdsaP256, 256),
                     (SshTestKeys.EcdsaBlob(ECDsa.Create(ECCurve.NamedCurves.nistP384)), SshPublicKey.EcdsaP384, 384),
                     (SshTestKeys.EcdsaBlob(ECDsa.Create(ECCurve.NamedCurves.nistP521)), SshPublicKey.EcdsaP521, 521),
                     (SshTestKeys.RsaBlob(RSA.Create(3072)), SshPublicKey.Rsa, 3072),
                     (SshTestKeys.RsaBlob(RSA.Create(4096)), SshPublicKey.Rsa, 4096),
                 })
        {
            // Spaces and tabs between the fields, a comment with spaces of its own, a line end (a pasted file).
            var key = SshPublicKey.Parse($"  {type}\t{Convert.ToBase64String(blob)}  my  laptop \r\n").Value;
            Assert.Equal((type, bits, "my  laptop"), (key.Type, key.Bits, key.Comment));
            Assert.Equal(blob, key.Blob);
            Assert.Equal("SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='), key.Fingerprint);
            Assert.Null(SshPublicKey.Parse($"{type} {Convert.ToBase64String(blob)}").Value.Comment);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 ")]
    [InlineData("ssh-ed25519 not*base64")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UA")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF\nssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF")]
    [InlineData("no-pty ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF")]
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF")]
    public void TextThatIsNotOneKeyLineIsRefused(string? text)
    {
        Assert.Equal(AccessErrors.SshKeyInvalid, SshPublicKey.Parse(text).Error);
    }

    [Fact]
    public void BlobsThatAreMalformedOrNotOnTheirCurveAreRefused()
    {
        var ed25519 = SshTestKeys.NewEd25519().PublicKey;
        var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportParameters(false).Q;
        var rsa = RSA.Create(2048).ExportParameters(false);
        var offCurve = Point(p256.X!, p256.Y!);
        offCurve[^1] ^= 1;
        var compressed = new byte[] { (byte)(0x02 | (p256.Y![^1] & 1)) }.Concat(p256.X!).ToArray();
        var identity = new byte[32];
        identity[0] = 1;
        byte[] even = [.. rsa.Modulus!];
        even[^1] &= 0xFE;

        foreach (var (name, blob) in new (string, byte[])[]
                 {
                     ("short Ed25519 key", SshTestKeys.Blob(Ascii(SshPublicKey.Ed25519), ed25519[..31])),
                     ("long Ed25519 key", SshTestKeys.Blob(Ascii(SshPublicKey.Ed25519), [.. ed25519, 0])),
                     ("Ed25519 key that does not decode", SshTestKeys.Blob(Ascii(SshPublicKey.Ed25519), Enumerable.Repeat((byte)0xFF, 32).ToArray())),
                     ("Ed25519 identity (small order)", SshTestKeys.Blob(Ascii(SshPublicKey.Ed25519), identity)),
                     ("bytes after the key", [.. SshTestKeys.Ed25519Blob(ed25519), 0]),
                     ("length past the end", SshTestKeys.Ed25519Blob(ed25519)[..^1]),
                     ("point off the curve", SshTestKeys.Blob(Ascii(SshPublicKey.EcdsaP256), Ascii("nistp256"), offCurve)),
                     ("compressed point", SshTestKeys.Blob(Ascii(SshPublicKey.EcdsaP256), Ascii("nistp256"), compressed)),
                     ("other curve's name", SshTestKeys.Blob(Ascii(SshPublicKey.EcdsaP256), Ascii("nistp384"), Point(p256.X!, p256.Y!))),
                     ("point of another curve's size", SshTestKeys.Blob(Ascii(SshPublicKey.EcdsaP384), Ascii("nistp384"), Point(p256.X!, p256.Y!))),
                     ("RSA modulus with a needless zero", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint(rsa.Exponent!), [0, 0, .. rsa.Modulus!])),
                     ("negative RSA modulus", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint(rsa.Exponent!), rsa.Modulus!)),
                     ("even RSA modulus", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint(rsa.Exponent!), SshTestKeys.Mpint(even))),
                     ("even RSA exponent", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint([1, 0, 0]), SshTestKeys.Mpint(rsa.Modulus!))),
                     ("RSA exponent 1", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint([1]), SshTestKeys.Mpint(rsa.Modulus!))),
                     ("RSA exponent of 5 bytes", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint([1, 0, 0, 0, 1]), SshTestKeys.Mpint(rsa.Modulus!))),
                     ("zero RSA exponent", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), [], SshTestKeys.Mpint(rsa.Modulus!))),
                     ("RSA modulus past OpenSSH's size", SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint(rsa.Exponent!), SshTestKeys.Mpint(OddBytes(2051)))),
                 })
        {
            var type = Encoding.ASCII.GetString(blob, 4, (int)BinaryPrimitives.ReadUInt32BigEndian(blob));
            Assert.True(SshPublicKey.Parse($"{type} {Convert.ToBase64String(blob)}").Error == AccessErrors.SshKeyInvalid, name);
            Assert.False(SshPublicKey.FromBlob(blob).IsSuccess, name);
        }
    }

    [Fact]
    public void TheTypeBeforeTheKeyMustBeTheOneInIt()
    {
        var blob = SshTestKeys.EcdsaBlob(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        Assert.Equal(AccessErrors.SshKeyInvalid, SshPublicKey.Parse($"{SshPublicKey.EcdsaP384} {Convert.ToBase64String(blob)}").Error);
        Assert.Equal(AccessErrors.SshKeyInvalid, SshPublicKey.Parse($"{SshPublicKey.Ed25519} {Convert.ToBase64String(blob)}").Error);
    }

    [Fact]
    public void OversizedTextIsRefusedBeforeItIsDecoded()
    {
        var huge = "ssh-ed25519 " + new string('A', SshPublicKey.MaxTextLength);
        Assert.Equal(AccessErrors.SshKeyInvalid, SshPublicKey.Parse(huge).Error);

        // A key field longer than any accepted blob, in a line that is not too long.
        var blob = SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint([1, 0, 1]), SshTestKeys.Mpint(OddBytes(SshPublicKey.MaxBlobLength)));
        Assert.True(Convert.ToBase64String(blob).Length < SshPublicKey.MaxTextLength);
        Assert.Equal(AccessErrors.SshKeyInvalid, SshPublicKey.Parse("ssh-rsa " + Convert.ToBase64String(blob)).Error);
        Assert.Equal(AccessErrors.SshKeyInvalid, SshPublicKey.FromBlob(blob).Error);
    }

    [Fact]
    public void WeakAndRefusedKindsOfKeysSayWhy()
    {
        Assert.Equal(AccessErrors.SshKeyRsaTooShort, SshPublicKey.Parse(Rsa1024).Error);
        var bits2047 = SshTestKeys.Blob(Ascii(SshPublicKey.Rsa), SshTestKeys.Mpint([1, 0, 1]), SshTestKeys.Mpint(OddBytes(256)));
        Assert.Equal(AccessErrors.SshKeyRsaTooShort, SshPublicKey.Parse($"ssh-rsa {Convert.ToBase64String(bits2047)}").Error);
        Assert.Equal(AccessErrors.SshKeyRsaTooShort, SshPublicKey.FromBlob(bits2047).Error);
        Assert.Equal(AccessErrors.SshKeyUnsupported, SshPublicKey.Parse(Dsa).Error);

        // A security key (FIDO) and a certificate, whatever follows.
        var ed25519 = SshTestKeys.NewEd25519().PublicKey;
        var sk = SshTestKeys.Blob(Ascii("sk-ssh-ed25519@openssh.com"), ed25519, Ascii("ssh:"));
        Assert.Equal(AccessErrors.SshKeyUnsupported, SshPublicKey.Parse($"sk-ssh-ed25519@openssh.com {Convert.ToBase64String(sk)} yubikey").Error);
        Assert.Equal(AccessErrors.SshKeyUnsupported, SshPublicKey.Parse("sk-ecdsa-sha2-nistp256@openssh.com AAAA").Error);
        Assert.Equal(AccessErrors.SshKeyUnsupported, SshPublicKey.Parse("ssh-ed25519-cert-v01@openssh.com AAAA").Error);
        Assert.Equal(AccessErrors.SshKeyUnsupported, SshPublicKey.FromBlob(sk).Error);
        Assert.Equal(AccessErrors.SshKeyUnsupported, SshPublicKey.FromBlob(Convert.FromBase64String(Dsa.Split(' ')[1])).Error);
    }

    [Fact]
    public void APrivateKeyOrAnotherFormatIsNamedAsSuch()
    {
        using var rsa = RSA.Create(2048);
        Assert.Equal(AccessErrors.SshKeyPrivate, SshPublicKey.Parse(rsa.ExportPkcs8PrivateKeyPem()).Error);
        Assert.Equal(AccessErrors.SshKeyPrivate, SshPublicKey.Parse("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmU=\n-----END OPENSSH PRIVATE KEY-----").Error);
        Assert.Equal(AccessErrors.SshKeyPrivate, SshPublicKey.Parse("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none").Error);
        Assert.Equal(AccessErrors.SshKeyWrongFormat, SshPublicKey.Parse(
            "---- BEGIN SSH2 PUBLIC KEY ----\nComment: \"ed25519-key-20261019\"\nAAAAC3NzaC1lZDI1NTE5AAAAIJODCRrRZ4j1+iszm3o/GYD74tq7eiRucOOuCWPu2UAF\n---- END SSH2 PUBLIC KEY ----").Error);
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] Point(byte[] x, byte[] y) => [0x04, .. x, .. y];

    private static byte[] OddBytes(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        bytes[0] = 0x7F;
        bytes[^1] |= 1;
        return bytes;
    }
}

/// <summary>Key blobs in SSH's wire format, and keys to make them of.</summary>
internal static class SshTestKeys
{
    public static (byte[] PrivateKey, byte[] PublicKey) NewEd25519()
    {
        var privateKey = new byte[Ed25519.SecretKeySize];
        Ed25519.GeneratePrivateKey(new SecureRandom(), privateKey);
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
        return (privateKey, publicKey);
    }

    public static byte[] Ed25519Blob(byte[] publicKey) => Blob(Encoding.ASCII.GetBytes(SshPublicKey.Ed25519), publicKey);

    public static byte[] EcdsaBlob(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var curve = parameters.Q.X!.Length switch
        {
            32 => "nistp256",
            48 => "nistp384",
            _ => "nistp521",
        };
        return Blob(Encoding.ASCII.GetBytes("ecdsa-sha2-" + curve), Encoding.ASCII.GetBytes(curve), [0x04, .. parameters.Q.X, .. parameters.Q.Y!]);
    }

    public static byte[] RsaBlob(RSA key)
    {
        var parameters = key.ExportParameters(false);
        return Blob(Encoding.ASCII.GetBytes(SshPublicKey.Rsa), Mpint(parameters.Exponent!), Mpint(parameters.Modulus!));
    }

    /// <summary>An unsigned big-endian number as a positive mpint: no leading zeros, one where the top bit is set.</summary>
    public static byte[] Mpint(byte[] unsigned)
    {
        var trimmed = unsigned.SkipWhile(b => b == 0).ToArray();
        return trimmed.Length > 0 && (trimmed[0] & 0x80) != 0 ? [0, .. trimmed] : trimmed;
    }

    /// <summary>Each field as an SSH string (its length first).</summary>
    public static byte[] Blob(params byte[][] fields)
    {
        var blob = new List<byte>();
        foreach (var field in fields)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)field.Length);
            blob.AddRange(length);
            blob.AddRange(field);
        }

        return [.. blob];
    }
}
