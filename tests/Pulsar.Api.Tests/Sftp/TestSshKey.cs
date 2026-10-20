using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Security;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Renci.SshNet;
using Renci.SshNet.Security;
using Ed25519 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// An SSH key pair made for a test: the private key for SSH.NET, the public key's line as users paste it (OpenSSH's
/// format) and its fingerprint as OpenSSH prints it.
/// </summary>
internal sealed class TestSshKey : IDisposable
{
    private TestSshKey(PrivateKeyFile file, string comment)
    {
        File = file;

        // SSH.NET writes the key's own blob (for RSA "ssh-rsa", whatever the signature algorithm).
        Blob = file.HostKeyAlgorithms.First().Data;
        Type = Encoding.ASCII.GetString(Blob, 4, (int)BinaryPrimitives.ReadUInt32BigEndian(Blob));
        Line = $"{Type} {Convert.ToBase64String(Blob)} {comment}";
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');
    }

    public PrivateKeyFile File { get; }

    public byte[] Blob { get; }

    public string Type { get; }

    public string Line { get; }

    public string Fingerprint { get; }

    public static TestSshKey Ed25519Key(string comment = "test@ed25519")
    {
        var seed = new byte[Ed25519.SecretKeySize];
        Ed25519.GeneratePrivateKey(new SecureRandom(), seed);
        return new TestSshKey(new PrivateKeyFile(new ED25519Key(seed)), comment);
    }

    public static TestSshKey Ecdsa(ECCurve curve, string comment = "test@ecdsa")
    {
        using var key = ECDsa.Create(curve);
        return FromPem(key.ExportPkcs8PrivateKeyPem(), comment);
    }

    public static TestSshKey Rsa(int bits, string comment = "test@rsa")
    {
        using var key = RSA.Create(bits);
        return FromPem(key.ExportPkcs8PrivateKeyPem(), comment);
    }

    /// <summary>Adds the key to the account of <paramref name="user"/>'s session (the password, and the code when given).</summary>
    public async Task<SshKeyView> AddAsync(PanelClient user, string? name = null, string? code = null)
    {
        using var response = await user.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest(name, Line, AccessScenario.Password, code));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<SshKeyView>(AccessScenario.Json, SftpTesting.Ct))!;
    }

    public void Dispose() => File.Dispose();

    private static TestSshKey FromPem(string pem, string comment)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(pem));
        return new TestSshKey(new PrivateKeyFile(stream), comment);
    }
}
