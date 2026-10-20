using System.Buffers.Binary;
using System.Text;
using Microsoft.DevTunnels.Ssh.Messages;
using Org.BouncyCastle.Security;
using Pulsar.Api.Sftp;
using Buffer = Microsoft.DevTunnels.Ssh.Buffer;
using Ed25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// The Ed25519 public key algorithm the SSH library is given (<see cref="SftpEd25519"/>): it reads only valid keys,
/// verifies exactly the signatures of their private keys over exactly what was signed, never signs, and the server's
/// algorithms: the clients' key types, never SHA-1 RSA, and the <c>publickey</c> method.
/// </summary>
public sealed class SftpEd25519Tests
{
    [Fact]
    public void SignaturesOfTheKeyOverTheDataVerifyAndNothingElseDoes()
    {
        var (privateKey, publicKey) = NewKey();
        var algorithm = new SftpEd25519();
        var pair = algorithm.CreateKeyPair();
        pair.SetPublicKeyBytes(Blob(publicKey));
        Assert.Equal(Blob(publicKey), pair.GetPublicKeyBytes().ToArray());
        Assert.Equal(SftpEd25519.AlgorithmName, pair.KeyAlgorithmName);
        Assert.False(pair.HasPrivateKey);

        var data = Encoding.ASCII.GetBytes("session id and the user authentication request");
        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(privateKey, 0, data, 0, data.Length, signature, 0);
        using var verifier = algorithm.CreateVerifier(pair);
        Assert.True(verifier.Verify(Buffer.From(data), Buffer.From(signature)));

        // Another message, a changed signature, a cut one, another key's.
        Assert.False(verifier.Verify(Buffer.From([.. data, 0]), Buffer.From(signature)));
        var changed = signature.ToArray();
        changed[10] ^= 1;
        Assert.False(verifier.Verify(Buffer.From(data), Buffer.From(changed)));
        Assert.False(verifier.Verify(Buffer.From(data), Buffer.From(signature[..63])));
        var other = algorithm.CreateKeyPair();
        other.SetPublicKeyBytes(Blob(NewKey().PublicKey));
        using var otherVerifier = algorithm.CreateVerifier(other);
        Assert.False(otherVerifier.Verify(Buffer.From(data), Buffer.From(signature)));

        // The library reads the signature's name and bytes: only an Ed25519 one.
        Assert.Equal(signature, algorithm.ReadSignatureData(Buffer.From(SignatureBlob("ssh-ed25519", signature))).ToArray());
        Assert.Throws<ArgumentException>(() => algorithm.ReadSignatureData(Buffer.From(SignatureBlob("ssh-rsa", signature))));
    }

    [Fact]
    public void OnlyValidKeysAreReadAndNothingIsSigned()
    {
        var algorithm = new SftpEd25519();
        var identity = new byte[32];
        identity[0] = 1;
        foreach (var blob in new[]
                 {
                     Blob(identity),
                     Blob(NewKey().PublicKey[..31]),
                     [.. Blob(NewKey().PublicKey), 0],
                     Strings(Encoding.ASCII.GetBytes("ssh-rsa"), NewKey().PublicKey),
                 })
        {
            Assert.Throws<ArgumentException>(() => algorithm.CreateKeyPair().SetPublicKeyBytes(Buffer.From(blob)));
        }

        Assert.Throws<InvalidOperationException>(() => algorithm.CreateKeyPair().GetPublicKeyBytes());
        Assert.Throws<NotSupportedException>(() => algorithm.GenerateKeyPair());
        Assert.Throws<NotSupportedException>(() => algorithm.CreateSigner(algorithm.CreateKeyPair()));
    }

    [Fact]
    public void TheServerTakesTheClientsKeyTypesAndPublicKeySignIns()
    {
        var configuration = SftpHost.CreateConfiguration();
        Assert.Equal(["ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-ed25519", "rsa-sha2-512", "rsa-sha2-256"],
            configuration.PublicKeyAlgorithms.Select(a => a!.Name));
        Assert.DoesNotContain(configuration.PublicKeyAlgorithms, a => a!.Name is "ssh-rsa" or "ssh-dss");
        Assert.Equal([AuthenticationMethods.PublicKey, AuthenticationMethods.KeyboardInteractive, AuthenticationMethods.Password],
            configuration.AuthenticationMethods);
    }

    private static (byte[] PrivateKey, byte[] PublicKey) NewKey()
    {
        var privateKey = new byte[Ed25519.SecretKeySize];
        Ed25519.GeneratePrivateKey(new SecureRandom(), privateKey);
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
        return (privateKey, publicKey);
    }

    private static byte[] Blob(byte[] publicKey) => Strings(Encoding.ASCII.GetBytes("ssh-ed25519"), publicKey);

    private static byte[] SignatureBlob(string name, byte[] signature) => Strings(Encoding.ASCII.GetBytes(name), signature);

    private static byte[] Strings(params byte[][] fields)
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
