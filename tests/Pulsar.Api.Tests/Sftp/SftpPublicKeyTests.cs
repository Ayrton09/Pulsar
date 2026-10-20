using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;
using static Pulsar.Api.Tests.Sftp.SftpTesting;
using Algorithms = Microsoft.DevTunnels.Ssh.Algorithms;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// Signing in to SFTP with an SSH key the user added (docs/guides/sftp.md, "SSH keys"), driven by SSH.NET: Ed25519,
/// ECDSA and RSA keys sign in alone, also for a two-factor user; keys the user has not added, SHA-1 RSA signatures,
/// disabled, locked-out and unprepared users are refused; wrong keys never lock the account; a deleted key's sessions
/// end; and the host key stays the ECDSA P-256 one.
/// </summary>
public sealed class SftpPublicKeyTests(SftpApiFactory factory) : IClassFixture<SftpApiFactory>
{
    // Sftp:MaxKeyAttemptsPerConnection's default.
    private static readonly int s_keyLimit = new SftpOptions().MaxKeyAttemptsPerConnection;

    [DatabaseFact]
    public async Task KeysOfEveryAcceptedTypeSignInAndTheHostKeyStaysTheSame()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "keys-types");
        var server = await InstalledAsync(scenario.Admin, agent, "Key Server");
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "server.cfg"), "by key", Ct);
        var role = await ServerRoleAsync(scenario, "keys-types-files", Permissions.Server.View, Permissions.Server.FilesView);
        var kim = await scenario.CreateUserAsync("keys.kim");
        await GrantAsync(scenario.Admin, kim.Id, server.Id, role.Id);
        using var account = await scenario.SignInAsync("keys.kim");
        using var ed25519 = TestSshKey.Ed25519Key();
        using var p256 = TestSshKey.Ecdsa(ECCurve.NamedCurves.nistP256);
        using var p384 = TestSshKey.Ecdsa(ECCurve.NamedCurves.nistP384);
        using var p521 = TestSshKey.Ecdsa(ECCurve.NamedCurves.nistP521);
        using var rsa = TestSshKey.Rsa(3072);
        var port = await factory.PortAsync();

        foreach (var key in new[] { ed25519, p256, p384, p521, rsa })
        {
            var added = await key.AddAsync(account, name: key.Type);
            Assert.Equal((key.Type, key.Fingerprint), (added.Type, added.Fingerprint));
            using var client = KeyClient(port, "keys.kim", key);
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            Assert.Equal("ecdsa-sha2-nistp256", client.ConnectionInfo.CurrentHostKeyAlgorithm);
            Assert.Equal([server.Folder], await Run(() => client.ListDirectory("/").Select(f => f.Name).Where(n => n is not "." and not "..").ToList()));
        }

        // A server's folder as the root works with a key too.
        using (var client = KeyClient(port, $"keys.kim.{server.ShortId}", ed25519))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            Assert.Equal("by key", await Run(() => client.ReadAllText("/server.cfg")));
        }

        // Each key's last use is shown, and each sign-in is audited with the key.
        var listed = (await account.Http.GetFromJsonAsync<List<SshKeyView>>("/api/v1/account/ssh-keys", AccessScenario.Json, Ct))!;
        Assert.Equal(5, listed.Count);
        Assert.All(listed, k => Assert.NotNull(k.LastUsedAt));
        Assert.Equal([256, 256, 384, 521, 3072], listed.OrderBy(k => k.Bits).Select(k => k.Bits));
        var signIns = await AuditAsync(AuditActions.Auth.Login);
        foreach (var key in new[] { ed25519, p256, p384, p521, rsa })
        {
            Assert.Contains(signIns, e => e.ActorUserId?.Value == kim.Id && Field(e, "method") == "publickey" && Field(e, "fingerprint") == key.Fingerprint
                && Field(e, "channel") == "sftp" && Field(e, "secondFactor") is null);
        }

        // The server still offers its ECDSA P-256 host key alone: the clients' key algorithms are not host key algorithms.
        Assert.Equal(["ecdsa-sha2-nistp256"], await HostKeyAlgorithmsAsync(port));
    }

    [DatabaseFact]
    public async Task KeysTheUserHasNotAddedAreRefusedAndNeverLockTheAccount()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var lena = await scenario.CreateUserAsync("keys-lena");
        await scenario.CreateUserAsync("keys-mia");
        using var account = await scenario.SignInAsync("keys-lena");
        using var added = TestSshKey.Ed25519Key();
        using var other = TestSshKey.Ecdsa(ECCurve.NamedCurves.nistP256);
        await added.AddAsync(account, "Laptop");
        var port = await factory.PortAsync();

        // Another key of hers, her key for another user or for nobody: the same refusal.
        for (var i = 0; i < 6; i++)
        {
            await FailsAsync(KeyClient(port, "keys-lena", other));
        }

        await FailsAsync(KeyClient(port, "keys-mia", added));
        await FailsAsync(KeyClient(port, "keys-nobody", added));
        var failures = (await AuditAsync(AuditActions.Auth.LoginFailed)).Where(e => Field(e, "method") == "publickey").ToList();
        Assert.Contains(failures, e => e.TargetId == lena.Id.ToString() && Field(e, "reason") == "invalid_key" && Field(e, "fingerprint") == other.Fingerprint);
        Assert.Contains(failures, e => e.TargetId is null && Field(e, "reason") == "invalid_key" && Field(e, "fingerprint") == added.Fingerprint);

        // Wrong keys are not wrong passwords: nothing counted, and her key still signs her in.
        Assert.Equal(0, (await UserAsync("keys-lena")).AccessFailedCount);
        using (var client = KeyClient(port, "keys-lena", added))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        }

        // Two keys offered: the client asks about each first, and only hers is said to do; the question is not a failure.
        var before = failures.Count;
        using (var client = KeyClient(port, "keys-lena", other, added))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        }

        await FailsAsync(KeyClient(port, "keys-nobody", other, added));
        Assert.Equal(before, (await AuditAsync(AuditActions.Auth.LoginFailed)).Count(e => Field(e, "method") == "publickey"));

        // A key sign-in leaves wrong passwords counted: it says nothing about whoever guesses them.
        await FailsAsync(PasswordClient(port, "keys-lena", "wrong password"));
        using (var client = KeyClient(port, "keys-lena", added))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        }

        Assert.Equal(1, (await UserAsync("keys-lena")).AccessFailedCount);
    }

    [DatabaseFact]
    public async Task RsaKeysSignInWithSha2OnlyNeverSha1()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("keys-nora");
        using var account = await scenario.SignInAsync("keys-nora");
        using var rsa = RSA.Create(2048);
        var pair = new Algorithms.Rsa.KeyPair(rsa);
        var line = $"ssh-rsa {Convert.ToBase64String(pair.GetPublicKeyBytes("ssh-rsa").ToArray())} nora";
        using (var response = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest(null, line, AccessScenario.Password, null)))
        {
            await AccessScenario.EnsureSuccessAsync(response);
        }

        var port = await factory.PortAsync();
        Assert.Equal((true, true), await SignsInAsync(port, "keys-nora", pair, new Algorithms.Rsa(Algorithms.Rsa.RsaWithSha256, "SHA2-256")));
        Assert.Equal((true, true), await SignsInAsync(port, "keys-nora", pair, new Algorithms.Rsa(Algorithms.Rsa.RsaWithSha512, "SHA2-512")));

        // The same key named "ssh-rsa" (the SHA-1 algorithm): unknown to the server, whatever the signature, and refused
        // as such: the connection stays, for the client's other keys or its password.
        var sha1 = new Algorithms.Rsa(Algorithms.Rsa.KeyAlgorithmName, "SHA2-256");
        Assert.Equal((false, true), await SignsInAsync(port, "keys-nora", pair, sha1));

        // Each such refusal counts as a question about a key: past the connection's bound, it ends.
        Assert.Equal((false, true), await SignsInAsync(port, "keys-nora", pair, sha1, attempts: s_keyLimit));
        Assert.Equal((false, false), await SignsInAsync(port, "keys-nora", pair, sha1, attempts: s_keyLimit + 1));
    }

    [DatabaseFact]
    public async Task AConnectionOffersAFewKeysAtMost()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("keys-uli");
        using var account = await scenario.SignInAsync("keys-uli");
        using var added = TestSshKey.Ed25519Key();
        await added.AddAsync(account);
        var others = Enumerable.Range(0, s_keyLimit).Select(_ => TestSshKey.Ed25519Key()).ToList();
        var port = await factory.PortAsync();
        try
        {
            // Her key asked about sixth: yes. Seventh: past the connection's bound, no without a look.
            using (var client = KeyClient(port, "keys-uli", [.. others.Take(s_keyLimit - 1), added]))
            {
                await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            }

            await FailsAsync(KeyClient(port, "keys-uli", [.. others, added]));
        }
        finally
        {
            others.ForEach(k => k.Dispose());
        }

        // Signed attempts with a key that is not hers: six are refused, the seventh ends the connection.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var pair = new Algorithms.ECDsa.KeyPair(ecdsa);
        Assert.Equal((false, true), await SignsInAsync(port, "keys-uli", pair, SshAlgorithms.PublicKey.ECDsaSha2Nistp384, attempts: s_keyLimit));
        Assert.Equal((false, false), await SignsInAsync(port, "keys-uli", pair, SshAlgorithms.PublicKey.ECDsaSha2Nistp384, attempts: s_keyLimit + 1));
    }

    [DatabaseFact]
    public async Task SignInRequestsOfAnyMethodAreBoundedPerConnection()
    {
        _ = await AccessScenario.CreateAsync(factory);
        var port = await factory.PortAsync();

        // A wrong password, then requests the library refuses uncounted: "none" and a method nobody offers.
        using var session = await RawClientSession.PastAWrongPasswordAsync(port);
        for (var i = 1; i < SftpServerSession.MaxAuthenticationRequests; i++)
        {
            await session.SendAsync(new AuthenticationRequestMessage("ssh-connection", i % 2 == 0 ? "none" : "no-such-method", "flood-nobody"));
        }

        // Each is answered, the last one allowed too, and the connection stays; one more ends it at once (well before the
        // sign-in deadline, 30 seconds from the connection, would).
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(session.Refusals == SftpServerSession.MaxAuthenticationRequests - 1),
            "every request answered");
        Assert.False(session.IsClosed);
        await session.SendAsync(new AuthenticationRequestMessage("ssh-connection", "none", "flood-nobody"));
        await session.Ended.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [DatabaseFact]
    public async Task TheServersOwnSignInMessagesFromAClientEndItsConnection()
    {
        _ = await AccessScenario.CreateAsync(factory);
        var port = await factory.PortAsync();
        var stayed = new List<string>();

        // (Message 60, a server's PK_OK or INFO_REQUEST, never reaches the session: the library answers it as unimplemented.)
        foreach (var message in new SshMessage[] { new AuthenticationSuccessMessage(), new AuthenticationFailureMessage { MethodNames = ["password"] } })
        {
            using var session = await RawClientSession.PastAWrongPasswordAsync(port);
            await session.SendAsync(message);
            if (await Task.WhenAny(session.Ended, Task.Delay(TimeSpan.FromSeconds(5), Ct)) != session.Ended)
            {
                stayed.Add(message.GetType().Name);
            }
        }

        Assert.Empty(stayed);

        // The server goes on: the next client gets its answer.
        using var after = await RawClientSession.PastAWrongPasswordAsync(port);
        Assert.False(after.IsClosed);
    }

    [DatabaseFact]
    public async Task DisabledLockedOutAndUnpreparedUsersAreRefusedTheirKeys()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var role = await scenario.CreateRoleAsync("keys-2fa-role", Permissions.Audit.View);
        var olga = await scenario.CreateUserAsync("keys-olga");
        var pia = await scenario.CreateUserAsync("keys-pia");
        var quinn = await scenario.CreateUserAsync("keys-quinn", role);
        using var olgaKey = TestSshKey.Ed25519Key();
        using var piaKey = TestSshKey.Ed25519Key();
        using var quinnKey = TestSshKey.Ed25519Key();
        foreach (var (name, key) in new[] { ("keys-olga", olgaKey), ("keys-pia", piaKey), ("keys-quinn", quinnKey) })
        {
            using var account = await scenario.SignInAsync(name);
            await key.AddAsync(account);
        }

        var port = await factory.PortAsync();
        using (var disabled = await scenario.Admin.PutAsync($"/api/v1/users/{olga.Id}", new UpdateUserRequest(null, null, IsDisabled: true)))
        {
            await AccessScenario.EnsureSuccessAsync(disabled);
        }

        // Five wrong passwords lock the account: the key waits as long.
        for (var i = 0; i < 5; i++)
        {
            await FailsAsync(PasswordClient(port, "keys-pia", "wrong " + i));
        }

        // A role now requires two-factor authentication, which Quinn has not set up yet.
        using (var required = await scenario.Admin.PutAsync($"/api/v1/roles/{role.Id}/two-factor", new SetRoleTwoFactorRequest(true)))
        {
            await AccessScenario.EnsureSuccessAsync(required);
        }

        await FailsAsync(KeyClient(port, "keys-olga", olgaKey));
        await FailsAsync(KeyClient(port, "keys-pia", piaKey));
        await FailsAsync(KeyClient(port, "keys-quinn", quinnKey));
        var failures = (await AuditAsync(AuditActions.Auth.LoginFailed)).Where(e => Field(e, "method") == "publickey").ToList();
        Assert.Contains(failures, e => e.TargetId == olga.Id.ToString() && Field(e, "reason") == "not_allowed");
        Assert.Contains(failures, e => e.TargetId == pia.Id.ToString() && Field(e, "reason") == "locked_out");
        Assert.Contains(failures, e => e.TargetId == quinn.Id.ToString() && Field(e, "reason") == "two_factor_setup_required");
    }

    [DatabaseFact]
    public async Task ATwoFactorUserSignsInWithTheKeyAlone()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var rosa = await scenario.CreateUserAsync("keys-rosa");
        var (secret, _) = await EnableTwoFactorAsync(scenario, "keys-rosa");
        using var account = await SignInWithCodeAsync(factory, "keys-rosa", secret);
        using var key = TestSshKey.Ecdsa(ECCurve.NamedCurves.nistP384);
        await key.AddAsync(account, code: Totp.Compute(secret, DateTimeOffset.UtcNow.AddSeconds(30)));
        var port = await factory.PortAsync();

        // No code asked: the key is the second factor; the password alone still never gets in.
        using (var client = KeyClient(port, "keys-rosa", key))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        }

        await FailsAsync(PasswordClient(port, "keys-rosa", AccessScenario.Password));
        Assert.Contains(await AuditAsync(AuditActions.Auth.Login), e => e.ActorUserId?.Value == rosa.Id && Field(e, "method") == "publickey"
            && Field(e, "fingerprint") == key.Fingerprint && Field(e, "secondFactor") is null);
    }

    [DatabaseFact]
    public async Task ADeletedKeyEndsTheSessionsItOpenedAndSignsInNoMore()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var sam = await scenario.CreateUserAsync("keys-sam");
        using var account = await scenario.SignInAsync("keys-sam");
        using var first = TestSshKey.Ed25519Key();
        using var second = TestSshKey.Rsa(2048);
        var firstAdded = await first.AddAsync(account, "First");
        await second.AddAsync(account, "Second");
        var port = await factory.PortAsync();
        using var byFirst = KeyClient(port, "keys-sam", first);
        using var bySecond = KeyClient(port, "keys-sam", second);
        await byFirst.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        await bySecond.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        using var byPassword = await ConnectedAsync(port, "keys-sam");

        // The user deletes one key: its session ends, the others stay.
        using (var deleted = await account.DeleteAsync($"/api/v1/account/ssh-keys/{firstAdded.Id}"))
        {
            Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(!byFirst.IsConnected), "the deleted key's session ended");
        Assert.Empty(await Run(() => bySecond.ListDirectory("/").Where(f => f.Name is not "." and not "..").ToList()));
        Assert.True(bySecond.IsConnected);
        await FailsAsync(KeyClient(port, "keys-sam", first));

        // An administrator deletes all of them: every session a key opened ends, the password's stays.
        using (var all = await scenario.Admin.DeleteAsync($"/api/v1/users/{sam.Id}/ssh-keys"))
        {
            await AccessScenario.EnsureSuccessAsync(all);
            Assert.Equal(0, (await all.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!.SshKeys);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(!bySecond.IsConnected), "the key sessions ended");
        Assert.Empty(await Run(() => byPassword.ListDirectory("/").Where(f => f.Name is not "." and not "..").ToList()));
        Assert.True(byPassword.IsConnected);
        await FailsAsync(KeyClient(port, "keys-sam", second));
    }

    [DatabaseFact]
    public async Task ARowDeletedBehindTheApisBackEndsTheSessionAtTheNextCheck()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("keys-tom");
        using var account = await scenario.SignInAsync("keys-tom");
        using var key = TestSshKey.Ed25519Key();
        await key.AddAsync(account);
        var port = await factory.PortAsync();
        using var client = KeyClient(port, "keys-tom", key);
        await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            Assert.Equal(1, await db.SshKeys.Where(k => k.Fingerprint == key.Fingerprint).ExecuteDeleteAsync(Ct));
        }

        // The user is read again at most 15 seconds after the last check: the key's absence ends the session then.
        var waited = Stopwatch.StartNew();
        while (client.IsConnected)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "The session of a deleted key is still open.");
            try
            {
                await Run(() => client.ListDirectory("/").ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The session ended.
            }

            await Task.Delay(250, Ct);
        }
    }

    /// <summary>
    /// Whether a raw session signs in with <paramref name="pair"/> under the name of <paramref name="algorithm"/> (trying
    /// up to <paramref name="attempts"/> times), and is still connected afterwards.
    /// </summary>
    /// <summary>A client session that sends any message, as a client of someone else's might: the library keeps that to itself.</summary>
    private sealed class RawClientSession : SshClientSession
    {
        private static readonly MethodInfo s_send = typeof(SshSession).GetMethod("SendMessageAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(SshMessage), typeof(CancellationToken)], null)
            ?? throw new MissingMethodException(nameof(SshSession), "SendMessageAsync");

        private readonly TcpClient _tcp = new();
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refusals;
        private bool _raw;

        private RawClientSession()
            : base(new SshSessionConfiguration(), new TraceSource("sftp-raw-tests", SourceLevels.Off))
        {
            Closed += (_, _) => _ended.TrySetResult();
            Authenticating += (_, e) =>
            {
                if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
                {
                    e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
                }
            };
        }

        /// <summary>When the connection ended, by either side.</summary>
        public Task Ended => _ended.Task;

        /// <summary>The server's refusals of what was sent raw: kept from the library, which would take them for its own.</summary>
        public int Refusals => Volatile.Read(ref _refusals);

        /// <summary>Connected and past a wrong password (the client starts the sign-in service with it); raw from then on.</summary>
        public static async Task<RawClientSession> PastAWrongPasswordAsync(int port)
        {
            var session = new RawClientSession();
            try
            {
                await session._tcp.ConnectAsync("127.0.0.1", port, Ct);
                await session.ConnectAsync(session._tcp.GetStream(), Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
                var wrong = new SshClientCredentials("flood-nobody", "not the password");
                Assert.False(await session.AuthenticateAsync(wrong).WaitAsync(SftpApiFactory.Timeout, Ct));
                session._raw = true;
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public Task SendAsync(SshMessage message) => (Task)s_send.Invoke(this, [message, Ct])!;

        protected override Task HandleMessageAsync(SshMessage message, CancellationToken cancellation)
        {
            if (_raw && message is AuthenticationFailureMessage)
            {
                Interlocked.Increment(ref _refusals);
                return Task.CompletedTask;
            }

            return base.HandleMessageAsync(message, cancellation);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _tcp.Dispose();
            }
        }
    }

    private static async Task<(bool SignedIn, bool Connected)> SignsInAsync(int port, string login, Algorithms.IKeyPair pair,
        Algorithms.PublicKeyAlgorithm algorithm, int attempts = 1)
    {
        var configuration = new SshSessionConfiguration();
        configuration.PublicKeyAlgorithms.Clear();
        configuration.PublicKeyAlgorithms.Add(algorithm);
        configuration.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp256); // the server's host key
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, Ct);
        using var session = new SshClientSession(configuration, new TraceSource("sftp-key-tests", SourceLevels.Off));
        session.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
            {
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
            }
        };
        await session.ConnectAsync(tcp.GetStream(), Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        try
        {
            var signedIn = false;
            for (var i = 0; i < attempts && !signedIn; i++)
            {
                signedIn = await session.AuthenticateAsync(new SshClientCredentials(login, pair)).WaitAsync(SftpApiFactory.Timeout, Ct);
            }

            return (signedIn, !session.IsClosed);
        }
        catch (SshConnectionException)
        {
            return (false, false);
        }
    }

    /// <summary>The server host key algorithms of the server's key exchange offer (RFC 4253, 7.1), read off the wire.</summary>
    private static async Task<List<string>> HostKeyAlgorithmsAsync(int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, Ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync("SSH-2.0-PulsarTests\r\n"u8.ToArray(), Ct);
        var line = new List<byte>();
        while (line.Count < 255 && (line.Count < 2 || line[^2] != '\r' || line[^1] != '\n'))
        {
            var one = new byte[1];
            await stream.ReadExactlyAsync(one, Ct).AsTask().WaitAsync(SftpApiFactory.Timeout, Ct);
            line.Add(one[0]);
        }

        Assert.StartsWith("SSH-2.0-", Encoding.ASCII.GetString([.. line]), StringComparison.Ordinal);
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, Ct).AsTask().WaitAsync(SftpApiFactory.Timeout, Ct);
        var packet = new byte[BinaryPrimitives.ReadUInt32BigEndian(header) - 1];
        await stream.ReadExactlyAsync(packet, Ct).AsTask().WaitAsync(SftpApiFactory.Timeout, Ct);

        // Message 20 (KEXINIT), a 16-byte cookie, the key exchange algorithms, then the host key algorithms.
        Assert.Equal(20, packet[0]);
        var offset = 17;
        var kexLength = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset));
        offset += 4 + kexLength;
        var hostKeyLength = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset));
        return [.. Encoding.ASCII.GetString(packet, offset + 4, hostKeyLength).Split(',')];
    }

    /// <summary>A metadata field as text (jsonb spaces the stored JSON its own way).</summary>
    private static string? Field(AuditEntry entry, string name)
    {
        if (entry.Metadata is null)
        {
            return null;
        }

        using var metadata = System.Text.Json.JsonDocument.Parse(entry.Metadata);
        return metadata.RootElement.TryGetProperty(name, out var value) && value.ValueKind != System.Text.Json.JsonValueKind.Null ? value.ToString() : null;
    }

    private async Task<List<AuditEntry>> AuditAsync(string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking().Where(e => e.Action == action).ToListAsync(Ct);
    }

    private async Task<PulsarUser> UserAsync(string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<PulsarUser>>().FindByNameAsync(userName))!;
    }
}
