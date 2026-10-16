using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Access;
using static Pulsar.Api.Tests.Sftp.SftpTesting;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// The panel's own <c>aes256-ctr</c> (<see cref="SftpAesCtr"/>) through the real SFTP server, with a client that offers
/// nothing else: each encrypt-then-MAC HMAC-SHA2, a few MiB uploaded and downloaded with matching hashes, across several
/// key exchanges (the keys and the counter are new after each one).
/// </summary>
public sealed class SftpAesCtrTransferTests(SftpApiFactory factory) : IClassFixture<SftpApiFactory>
{
    private const int Chunk = 32 * 1024;
    private const int Size = 4 * 1024 * 1024;
    private const int KeyRotationBytes = 1024 * 1024;

    [DatabaseTheory]
    [InlineData("hmac-sha2-256-etm@openssh.com")]
    [InlineData("hmac-sha2-512-etm@openssh.com")]
    public async Task AFewMebibytesGoBothWaysWithOurCtrAndEachEncryptThenMacHmac(string mac)
    {
        var name = $"sftp-ctr-{mac[10..13]}";
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, name);
        var server = await InstalledAsync(scenario.Admin, agent, name);
        var role = await ServerRoleAsync(scenario, name, Permissions.Server.View, Permissions.Server.FilesView, Permissions.Server.FilesEdit);
        var user = await scenario.CreateUserAsync(name);
        await GrantAsync(scenario.Admin, user.Id, server.Id, role.Id);

        var configuration = new SshSessionConfiguration(useSecurity: true, enableCompression: false, enableReconnect: false);
        Only(configuration.EncryptionAlgorithms, SftpAesCtr.AlgorithmName);
        Only(configuration.HmacAlgorithms, mac);
        Only(configuration.KeyExchangeAlgorithms, "ecdh-sha2-nistp384");
        SetRotation(configuration);
        var trace = new TraceSource(name, SourceLevels.All);
        trace.Listeners.Clear();
        var traced = new TraceLines();
        trace.Listeners.Add(traced);

        var content = RandomNumberGenerator.GetBytes(Size);
        using var sftp = await RawSftpAsync(await factory.PortAsync(), $"{name}.{server.ShortId}", configuration, trace);

        // A key exchange started by the client alone ends the connection with the library ("Key exchange not started"):
        // both sides start one, so the server's threshold (512 MiB) is lowered as the client's is.
        var serverSession = ServerSession(user.Id);
        SetRotation(serverSession.Config);

        var handle = await sftp.OpenAsync("/ctr.bin", SftpProtocol.OpenWrite | SftpProtocol.OpenCreate | SftpProtocol.OpenTruncate);
        for (var offset = 0; offset < content.Length; offset += Chunk)
        {
            Assert.Equal(SftpProtocol.Ok, await sftp.WriteAsync(handle, (ulong)offset, content[offset..Math.Min(offset + Chunk, content.Length)]));
        }

        Assert.Equal(SftpProtocol.Ok, await sftp.CloseAsync(handle));
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(server.Directory, "ctr.bin"), Ct)));

        handle = await sftp.OpenAsync("/ctr.bin", SftpProtocol.OpenRead);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long downloaded = 0;
        while (await sftp.ReadAsync(handle, (ulong)downloaded, Chunk) is { } data)
        {
            hash.AppendData(data);
            downloaded += data.Length;
        }

        Assert.Equal(Size, downloaded);
        Assert.Equal(SHA256.HashData(content), hash.GetHashAndReset());
        Assert.Equal(SftpProtocol.Ok, await sftp.CloseAsync(handle));

        // What was negotiated, at every key exchange, is what the client offered, and the server runs our cipher in both directions.
        var negotiated = traced.Matching("negotiation:").Where(l => l.Contains("Encryption", StringComparison.Ordinal) || l.Contains("Hmac", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(negotiated);
        Assert.All(negotiated, l => Assert.EndsWith($"=> {(l.Contains("Encryption", StringComparison.Ordinal) ? SftpAesCtr.AlgorithmName : mac)}", l, StringComparison.Ordinal));
        var (cipher, decipher) = ServerCiphers(serverSession);
        Assert.Equal(typeof(SftpAesCtr.Cipher), cipher);
        Assert.Equal(typeof(SftpAesCtr.Cipher), decipher);

        // The first key exchange and several more (each is a NEWKEYS sent and one received).
        var newKeys = traced.Matching("NewKeysMessage").Count;
        Assert.True(newKeys >= 8, $"Only {newKeys} NEWKEYS messages for {2 * Size} bytes.");
    }

    private static void Only<T>(ICollection<T?> algorithms, string name)
        where T : SshAlgorithm
    {
        foreach (var other in algorithms.Where(a => a is null || a.Name != name).ToList())
        {
            algorithms.Remove(other);
        }

        Assert.Single(algorithms);
    }

    /// <summary>Bytes after which the session starts a key exchange (an internal setting of the SSH library).</summary>
    private static void SetRotation(SshSessionConfiguration configuration) =>
        typeof(SshSessionConfiguration).GetProperty("KeyRotationThreshold", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(configuration, KeyRotationBytes);

    private SshServerSession ServerSession(Guid user)
    {
        var all = (IEnumerable<SftpConnection>)typeof(SftpConnections).GetField("_all", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(factory.Services.GetRequiredService<SftpConnections>())!;
        return Assert.Single(all, c => c.Identity?.UserId.Value == user).Session!;
    }

    /// <summary>The types of the cipher and of the decipher the session uses now (internals of the SSH library).</summary>
    private static (Type Cipher, Type Decipher) ServerCiphers(SshServerSession session)
    {
        var any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var protocol = typeof(SshSession).GetProperty("Protocol", any)!.GetValue(session)!;
        var algorithms = protocol.GetType().GetProperty("Algorithms", any)!.GetValue(protocol)!;
        object Get(string property) => algorithms.GetType().GetProperty(property, any)!.GetValue(algorithms)!;
        return (Get("Cipher").GetType(), Get("Decipher").GetType());
    }

    /// <summary>What the library traces, line by line.</summary>
    private sealed class TraceLines : TraceListener
    {
        private readonly List<string> _lines = [];

        public List<string> Matching(string text)
        {
            lock (_lines)
            {
                return [.. _lines.Where(l => l.Contains(text, StringComparison.Ordinal))];
            }
        }

        public override void Write(string? message) => Add(message);

        public override void WriteLine(string? message) => Add(message);

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) => Add(message);

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
            Add(args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format!, args) : format);

        private void Add(string? line)
        {
            if (line is not null)
            {
                lock (_lines)
                {
                    _lines.Add(line);
                }
            }
        }
    }
}
