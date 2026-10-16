using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Pulsar.Api.Sftp;
using SshBuffer = Microsoft.DevTunnels.Ssh.Buffer;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>The panel's aes256-ctr gives the same bytes as the standard's test vectors and as the SSH library's own.</summary>
public sealed class SftpAesCtrTests
{
    [Fact]
    public void MatchesTheNistVectors()
    {
        // NIST SP 800-38A, F.5.5 CTR-AES256.Encrypt.
        var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var counter = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Convert.FromHexString(
            "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        var expected = Convert.FromHexString(
            "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c52b0930daa23de94ce87017ba2d84988ddfc9c58db67aada613c2dd08457941a6");

        using var cipher = new SftpAesCtr().CreateCipher(true, SshBuffer.From(key), SshBuffer.From(counter));
        var output = new byte[plaintext.Length];
        cipher.Transform(SshBuffer.From(plaintext), SshBuffer.From(output));
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData("000102030405060708090a0b0c0d0e0f")]
    [InlineData("0f0e0d0c0b0a0908fffffffffffffff0")] // the low 64 bits wrap within the stream
    [InlineData("fffffffffffffffffffffffffffffffe")] // so does the whole counter
    public void MatchesTheLibraryAcrossPacketsInBothDirections(string iv)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var counter = Convert.FromHexString(iv);
        using var ours = new SftpAesCtr().CreateCipher(true, SshBuffer.From(key), SshBuffer.From(counter));
        var library = new EncryptionAlgorithm("aes256-ctr", "AES", CipherModeEx.CTR, 256);
        using var theirs = library.CreateCipher(true, SshBuffer.From(key), SshBuffer.From(counter));
        using var decipher = new SftpAesCtr().CreateCipher(false, SshBuffer.From(key), SshBuffer.From(counter));

        // Packets of many sizes, a keystream chunk and more among them, as the session state carries over.
        foreach (var blocks in new[] { 1, 2, 3, 64, SftpAesCtr.ChunkBytes / 16, (SftpAesCtr.ChunkBytes / 16) + 1, 3 * SftpAesCtr.ChunkBytes / 16, 7 })
        {
            var packet = RandomNumberGenerator.GetBytes(blocks * 16);
            var expected = new byte[packet.Length];
            theirs.Transform(SshBuffer.From(packet), SshBuffer.From(expected));

            // In place, as the library does with what it receives.
            var actual = (byte[])packet.Clone();
            ours.Transform(SshBuffer.From(actual), SshBuffer.From(actual));
            Assert.Equal(expected, actual);

            decipher.Transform(SshBuffer.From(actual), SshBuffer.From(actual));
            Assert.Equal(packet, actual);
        }
    }

    [Fact]
    public void RefusesPartialBlocksAndShortKeys()
    {
        using var cipher = new SftpAesCtr().CreateCipher(true, SshBuffer.From(new byte[32]), SshBuffer.From(new byte[16]));
        Assert.Throws<ArgumentException>(() => cipher.Transform(SshBuffer.From(new byte[20]), SshBuffer.From(new byte[20])));
        Assert.Throws<ArgumentException>(() => cipher.Transform(SshBuffer.From(new byte[32]), SshBuffer.From(new byte[16])));
        Assert.Throws<ArgumentException>(() => new SftpAesCtr().CreateCipher(true, SshBuffer.From(new byte[16]), SshBuffer.From(new byte[16])));
        Assert.Throws<ArgumentException>(() => new SftpAesCtr().CreateCipher(true, SshBuffer.From(new byte[32]), SshBuffer.From(new byte[8])));
    }

    [Fact]
    public void TheHostOffersItInPlaceOfTheLibrarys()
    {
        var algorithms = SftpHost.CreateConfiguration().EncryptionAlgorithms.Select(a => (a!.Name, a.GetType())).ToList();
        Assert.Equal(
            [("aes256-gcm@openssh.com", typeof(Microsoft.DevTunnels.Ssh.Algorithms.AesGcm)), (SftpAesCtr.AlgorithmName, typeof(SftpAesCtr))],
            algorithms);
    }
}
