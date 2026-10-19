using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;
using Renci.SshNet.Common;
using static Pulsar.Api.Tests.Sftp.SftpTesting;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// The panel's SFTP server (Phase 17) driven by SSH.NET against the real agent core: sign-in as in the browser (lockout,
/// two-factor, disabled and unknown users), only the user's servers, permissions at every request, uploads, downloads,
/// resumes, renames and folders, what SFTP hides, and nothing but SFTP.
/// </summary>
public sealed class SftpServerTests(SftpApiFactory factory) : IClassFixture<SftpApiFactory>
{
    private static readonly string[] s_ciphers = ["aes256-gcm@openssh.com", "aes256-ctr"];

    [DatabaseFact]
    public async Task UsersSignInWithTheirPanelPasswordAndSeeOnlyTheirServers()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "sftp-own");
        var mine = await InstalledAsync(scenario.Admin, agent, "My Server");
        var theirs = await InstalledAsync(scenario.Admin, agent, "Their Server");
        await File.WriteAllTextAsync(Path.Combine(mine.Directory, "server.cfg"), "hostname mine", Ct);
        var role = await ServerRoleAsync(scenario, "sftp-own-files", Permissions.Server.View, Permissions.Server.FilesView, Permissions.Server.FilesEdit);
        var alice = await scenario.CreateUserAsync("sftp.alice");
        await GrantAsync(scenario.Admin, alice.Id, mine.Id, role.Id);
        var port = await factory.PortAsync();

        string? fingerprint = null;
        using (var client = Client(port, "sftp.alice", AccessScenario.Password, fingerprint: f => fingerprint = f))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            Assert.StartsWith("ecdh-sha2-nistp", client.ConnectionInfo.CurrentKeyExchangeAlgorithm, StringComparison.Ordinal);
            Assert.Equal("ecdsa-sha2-nistp256", client.ConnectionInfo.CurrentHostKeyAlgorithm);
            Assert.Contains(client.ConnectionInfo.CurrentServerEncryption, s_ciphers);
            var root = await Run(() => client.ListDirectory("/").Select(f => f.Name).Where(n => n is not "." and not "..").ToList());
            Assert.Equal([mine.Folder], root);
            Assert.Equal("hostname mine", await Run(() => client.ReadAllText($"/{mine.Folder}/server.cfg")));
            Assert.False(await Run(() => client.Exists($"/{theirs.Folder}")));
            await Assert.ThrowsAsync<SftpPermissionDeniedException>(() => Run(() => client.CreateDirectory("/new")));
        }

        // The fingerprint users are shown is the one the server presents.
        using (var user = await scenario.SignInAsync("sftp.alice"))
        {
            var hint = (await user.Http.GetFromJsonAsync<SftpConnectionResponse>("/api/v1/sftp", AccessScenario.Json, Ct))!;
            Assert.Equal(fingerprint, hint.HostKeyFingerprint);
            Assert.Equal("sftp.alice", hint.UserName);
            Assert.True(hint.Enabled);
        }

        // name.<shortId>: that server's directory is the root (the user name holds a dot of its own).
        using (var client = await ConnectedAsync(port, $"sftp.alice.{mine.ShortId}"))
        {
            Assert.Contains("server.cfg", await Run(() => client.ListDirectory("/").Select(f => f.Name).ToList()));
            Assert.Equal("hostname mine", await Run(() => client.ReadAllText("/server.cfg")));
        }

        // Another server's short id is no root of theirs, and the "password" method works for a user without two-factor.
        await FailsAsync(Client(port, $"sftp.alice.{theirs.ShortId}", AccessScenario.Password));
        using (var client = PasswordClient(port, "sftp.alice", AccessScenario.Password))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            Assert.True(client.IsConnected);
        }

        var signIns = await AuditAsync(AuditActions.Auth.Login);
        Assert.Contains(signIns, e => e.ActorUserId?.Value == alice.Id && Field(e, "channel") == "sftp"
            && e.UserAgent!.StartsWith("SFTP SSH-2.0-", StringComparison.Ordinal) && e.IpAddress is not null);
    }

    [DatabaseFact]
    public async Task TwoFactorUsersAreAskedForTheCodeAndThePasswordMethodNeverSignsThemIn()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "sftp-2fa");
        var server = await InstalledAsync(scenario.Admin, agent, "Two Factor");
        var role = await ServerRoleAsync(scenario, "sftp-2fa-files", Permissions.Server.View, Permissions.Server.FilesView);
        var bob = await scenario.CreateUserAsync("sftp-bob");
        await GrantAsync(scenario.Admin, bob.Id, server.Id, role.Id);
        var (key, recoveryCodes) = await EnableTwoFactorAsync(scenario, "sftp-bob");
        var port = await factory.PortAsync();

        var prompts = new List<string>();
        await FailsAsync(Client(port, "sftp-bob", AccessScenario.Password, code: "000000", prompts: prompts));
        Assert.Equal([SftpAuthenticator.PasswordPrompt, SftpAuthenticator.CodePrompt], prompts.Take(2));

        prompts.Clear();
        using (var client = Client(port, "sftp-bob", AccessScenario.Password, code: Totp.Compute(key, DateTimeOffset.UtcNow.AddSeconds(30)), prompts: prompts))
        {
            await client.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            Assert.Equal([SftpAuthenticator.PasswordPrompt, SftpAuthenticator.CodePrompt], prompts);
            Assert.Equal([server.Folder], await Run(() => client.ListDirectory("/").Select(f => f.Name).Where(n => n is not "." and not "..").ToList()));
        }

        // Recovery codes are for the browser only.
        await FailsAsync(Client(port, "sftp-bob", AccessScenario.Password, code: recoveryCodes[0]));

        // A wrong password gets no code prompt; the password method alone never gets in.
        prompts.Clear();
        await FailsAsync(Client(port, "sftp-bob", "not the password", code: "123456", prompts: prompts));
        Assert.DoesNotContain(SftpAuthenticator.CodePrompt, prompts);
        await FailsAsync(PasswordClient(port, "sftp-bob", AccessScenario.Password));
        Assert.Contains(await AuditAsync(AuditActions.Auth.TwoFactorFailed), e => e.TargetId == bob.Id.ToString());
        Assert.Contains(await AuditAsync(AuditActions.Auth.LoginFailed), e => e.Metadata.Contains("two_factor_required", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task WrongPasswordsLockTheAccountAndDisabledOrUnknownUsersGetTheSameAnswer()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var carol = await scenario.CreateUserAsync("sftp-carol");
        var dave = await scenario.CreateUserAsync("sftp-dave");
        var port = await factory.PortAsync();

        // Unknown users get the password prompt too.
        var prompts = new List<string>();
        await FailsAsync(Client(port, "sftp-nobody", "whatever", prompts: prompts));
        Assert.Equal(SftpAuthenticator.PasswordPrompt, prompts[0]);

        // Five wrong passwords lock the account, counted per check whatever the connections.
        for (var i = 0; i < 5; i++)
        {
            await FailsAsync(PasswordClient(port, "sftp-carol", "wrong " + i));
        }

        await FailsAsync(Client(port, "sftp-carol", AccessScenario.Password));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<PulsarUser>>();
            Assert.True(await users.IsLockedOutAsync((await users.FindByNameAsync("sftp-carol"))!));
        }

        using (var disabled = await scenario.Admin.PutAsync($"/api/v1/users/{dave.Id}", new UpdateUserRequest(null, null, IsDisabled: true)))
        {
            await AccessScenario.EnsureSuccessAsync(disabled);
        }

        await FailsAsync(Client(port, "sftp-dave", AccessScenario.Password));
        var failures = await AuditAsync(AuditActions.Auth.LoginFailed);
        Assert.Contains(failures, e => e.TargetId == carol.Id.ToString() && e.Metadata.Contains("locked_out", StringComparison.Ordinal));
        Assert.Contains(failures, e => e.TargetId == dave.Id.ToString() && e.Metadata.Contains("not_allowed", StringComparison.Ordinal));
        Assert.Contains(failures, e => e.TargetId is null && Field(e, "channel") == "sftp");
    }

    [DatabaseFact]
    public async Task FilesGoBothWaysResumeAndStayHidden()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "sftp-files");
        var server = await InstalledAsync(scenario.Admin, agent, "Files");
        var role = await ServerRoleAsync(scenario, "sftp-editor", Permissions.Server.View, Permissions.Server.FilesView, Permissions.Server.FilesEdit);
        var erin = await scenario.CreateUserAsync("sftp-erin");
        await GrantAsync(scenario.Admin, erin.Id, server.Id, role.Id);
        Directory.CreateDirectory(Path.Combine(server.Directory, ".pulsar"));
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "odd‮gnp.exe"), "x", Ct);
        var port = await factory.PortAsync();
        using var client = await ConnectedAsync(port, $"sftp-erin.{server.ShortId}");
        var content = RandomNumberGenerator.GetBytes((3 * 1024 * 1024) + 17);

        // Upload and download, whole.
        await Run(() => client.UploadFile(new MemoryStream(content), "/maps.bin"));
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(server.Directory, "maps.bin"), Ct)));
        using (var downloaded = new MemoryStream())
        {
            await Run(() => client.DownloadFile("/maps.bin", downloaded));
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(downloaded.ToArray()));
        }

        // A read from an offset (a resumed download), and a seek back.
        await Run(() =>
        {
            using var stream = client.OpenRead("/maps.bin");
            stream.Seek(1_000_000, SeekOrigin.Begin);
            var part = new byte[50_000];
            stream.ReadExactly(part);
            Assert.Equal(content[1_000_000..1_050_000], part);
            stream.Seek(10, SeekOrigin.Begin);
            stream.ReadExactly(part.AsSpan(0, 100));
            Assert.Equal(content[10..110], part[..100]);
        });

        // Resumed uploads: with the append flag, and by writing at the end of what is there.
        await File.WriteAllBytesAsync(Path.Combine(server.Directory, "resume1.bin"), content[..1_000_000], Ct);
        await File.WriteAllBytesAsync(Path.Combine(server.Directory, "resume2.bin"), content[..2_000_000], Ct);
        await Run(() =>
        {
            using (var appended = client.Open("/resume1.bin", FileMode.Append, FileAccess.Write))
            {
                appended.Write(content, 1_000_000, content.Length - 1_000_000);
            }

            using var atTheEnd = client.Open("/resume2.bin", FileMode.Open, FileAccess.Write);
            atTheEnd.Seek(2_000_000, SeekOrigin.Begin);
            atTheEnd.Write(content, 2_000_000, content.Length - 2_000_000);
        });
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(server.Directory, "resume1.bin"), Ct));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(server.Directory, "resume2.bin"), Ct));

        // Renames: never over an existing file, unless posix-rename says to (WinSCP's .filepart).
        await Run(() => client.WriteAllText("/server.cfg", "old"));
        await Run(() => client.WriteAllText("/server.cfg.filepart", "new"));
        await Assert.ThrowsAnyAsync<SshException>(() => Run(() => client.RenameFile("/server.cfg.filepart", "/server.cfg", isPosix: false)));
        await Run(() => client.RenameFile("/server.cfg.filepart", "/server.cfg", isPosix: true));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(server.Directory, "server.cfg"), Ct));
        var time = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await Run(() => client.SetLastWriteTimeUtc("/server.cfg", time));
        Assert.Equal(time, File.GetLastWriteTimeUtc(Path.Combine(server.Directory, "server.cfg")));

        // Folders: rmdir only removes an empty one; rm never removes a folder.
        await Run(() => client.CreateDirectory("/maps"));
        await Run(() => client.WriteAllText("/maps/de_test.cfg", "x"));
        await Assert.ThrowsAnyAsync<SshException>(() => Run(() => client.DeleteDirectory("/maps")));
        await Assert.ThrowsAnyAsync<SshException>(() => Run(() => client.DeleteFile("/maps")));
        Assert.True(File.Exists(Path.Combine(server.Directory, "maps", "de_test.cfg")));
        await Run(() => client.DeleteFile("/maps/de_test.cfg"));
        await Run(() => client.DeleteDirectory("/maps"));
        Assert.False(Directory.Exists(Path.Combine(server.Directory, "maps")));

        // What SFTP hides is not there, to read or to write.
        var names = await Run(() => client.ListDirectory("/").Select(f => f.Name).ToList());
        Assert.DoesNotContain(".pulsar", names);
        Assert.DoesNotContain(names, n => n.Contains('‮', StringComparison.Ordinal));
        Assert.False(await Run(() => client.Exists("/.pulsar")));
        await Assert.ThrowsAnyAsync<SshException>(() => Run(() => client.WriteAllText("/.pulsar/x.cfg", "x")));
        Assert.False(File.Exists(Path.Combine(server.Directory, ".pulsar", "x.cfg")));

        // '..' stops at the root.
        Assert.Contains("server.cfg", await Run(() => client.ListDirectory("/../..").Select(f => f.Name).ToList()));

        var audited = await AuditAsync(AuditActions.Server.FileWritten);
        Assert.Contains(audited, e => e.ActorUserId?.Value == erin.Id && e.ActorDisplay == "sftp-erin · SFTP" && Field(e, "append") == "True");
    }

    [DatabaseFact]
    public async Task WritesThatWouldCutAFileOrHoldTooMuchAndFoldersWithoutTheirParentAreRefused()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "sftp-writes");
        var server = await InstalledAsync(scenario.Admin, agent, "Writes");
        var role = await ServerRoleAsync(scenario, "sftp-writer", Permissions.Server.View, Permissions.Server.FilesView, Permissions.Server.FilesEdit);
        var jane = await scenario.CreateUserAsync("sftp-jane");
        await GrantAsync(scenario.Admin, jane.Id, server.Id, role.Id);
        var config = Path.Combine(server.Directory, "server.cfg");
        await File.WriteAllTextAsync(config, "hostname long enough", Ct);
        var port = await factory.PortAsync();
        var login = $"sftp-jane.{server.ShortId}";

        using (var client = await ConnectedAsync(port, login))
        {
            // Written from its start without truncation, for less than it holds: refused, the file stays as it was.
            await Assert.ThrowsAnyAsync<SshException>(() => Run(() =>
            {
                using var partial = client.Open("/server.cfg", FileMode.Open, FileAccess.Write);
                partial.Write("abc"u8);
            }));
            Assert.Equal("hostname long enough", await File.ReadAllTextAsync(config, Ct));
            await Run(() =>
            {
                using var whole = client.Open("/server.cfg", FileMode.Open, FileAccess.Write);
                whole.Write("HOSTNAME LONG ENOUGH"u8);
            });
            Assert.Equal("HOSTNAME LONG ENOUGH", await File.ReadAllTextAsync(config, Ct));

            // A folder goes in one that exists.
            await Assert.ThrowsAsync<SftpPathNotFoundException>(() => Run(() => client.CreateDirectory("/missing/maps")));
            Assert.False(Directory.Exists(Path.Combine(server.Directory, "missing")));
            await Run(() => client.CreateDirectory("/maps"));
            await Run(() => client.CreateDirectory("/maps/de"));
            Assert.True(Directory.Exists(Path.Combine(server.Directory, "maps", "de")));
        }

        // Writes ahead of a gap: a session keeps a bounded number of them, each costing more than its bytes.
        using var raw = await RawSftpAsync(port, login);
        const uint flags = SftpProtocol.OpenWrite | SftpProtocol.OpenCreate | SftpProtocol.OpenTruncate;
        var tiny = await raw.OpenAsync("/tiny.bin", flags);
        var accepted = 0;
        while (accepted <= SftpSession.MaxReorderEntries && await raw.WriteAsync(tiny, (ulong)(2 * (accepted + 1)), [1]) == SftpProtocol.Ok)
        {
            accepted++;
        }

        Assert.Equal(SftpSession.MaxReorderEntries, accepted);
        Assert.Equal(SftpProtocol.Failure, await raw.CloseAsync(tiny));

        var budget = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SftpOptions>>().Value.MaxReorderBytes;
        var size = budget / 200;
        var large = await raw.OpenAsync("/large.bin", flags);
        accepted = 0;
        while (accepted < 300 && await raw.WriteAsync(large, (ulong)((accepted + 1) * 2 * size), new byte[size]) == SftpProtocol.Ok)
        {
            accepted++;
        }

        Assert.Equal(budget / (size + SftpSession.ReorderEntryOverhead), accepted);
        Assert.Equal(SftpProtocol.Failure, await raw.CloseAsync(large));
        Assert.False(File.Exists(Path.Combine(server.Directory, "tiny.bin")));
        Assert.False(File.Exists(Path.Combine(server.Directory, "large.bin")));
    }

    [DatabaseFact]
    public async Task ViewersReadButChangeNothing()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "sftp-view");
        var server = await InstalledAsync(scenario.Admin, agent, "View Only");
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "server.cfg"), "keep", Ct);
        var role = await ServerRoleAsync(scenario, "sftp-viewer", Permissions.Server.View, Permissions.Server.FilesView);
        var frank = await scenario.CreateUserAsync("sftp-frank");
        await GrantAsync(scenario.Admin, frank.Id, server.Id, role.Id);
        var port = await factory.PortAsync();
        using var client = await ConnectedAsync(port, "sftp-frank");
        var file = $"/{server.Folder}/server.cfg";

        Assert.Equal("keep", await Run(() => client.ReadAllText(file)));
        await Assert.ThrowsAsync<SftpPermissionDeniedException>(() => Run(() => client.WriteAllText(file, "changed")));
        await Assert.ThrowsAsync<SftpPermissionDeniedException>(() => Run(() => client.UploadFile(new MemoryStream([1, 2]), $"/{server.Folder}/new.bin")));
        await Assert.ThrowsAsync<SftpPermissionDeniedException>(() => Run(() => client.CreateDirectory($"/{server.Folder}/maps")));
        await Assert.ThrowsAsync<SftpPermissionDeniedException>(() => Run(() => client.DeleteFile(file)));
        await Assert.ThrowsAsync<SftpPermissionDeniedException>(() => Run(() => client.RenameFile(file, $"/{server.Folder}/other.cfg")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(server.Directory, "server.cfg"), Ct));
        Assert.False(File.Exists(Path.Combine(server.Directory, "new.bin")));
        Assert.False(File.Exists(Path.Combine(server.Directory, "other.cfg")));
        Assert.False(Directory.Exists(Path.Combine(server.Directory, "maps")));
    }

    [DatabaseFact]
    public async Task NothingButSftpIsAccepted()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("sftp-gina");
        var port = await factory.PortAsync();
        var (session, tcp) = await RawSessionAsync(port, "sftp-gina", AccessScenario.Password);
        using (tcp)
        using (session)
        {
            foreach (var type in new[] { "shell", "exec", "pty-req", "env", "x11-req", "auth-agent-req@openssh.com" })
            {
                var channel = await session.OpenChannelAsync(Ct);
                Assert.False(await channel.RequestAsync(new ChannelRequestMessage { RequestType = type, WantReply = true }, Ct), type);
                await channel.CloseAsync(Ct);
            }

            var other = await session.OpenChannelAsync(Ct);
            Assert.False(await other.RequestAsync(new SubsystemRequestMessage { Subsystem = "shell", WantReply = true }, Ct));
            Assert.False(await session.RequestAsync(new SessionRequestMessage { RequestType = "tcpip-forward", WantReply = true }, Ct));
            await Assert.ThrowsAnyAsync<Exception>(() => session.OpenChannelAsync("direct-tcpip", Ct).WaitAsync(SftpApiFactory.Timeout, Ct));
        }
    }

    [DatabaseFact]
    public async Task SessionsEndWhenTheUserIsDisabled()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var henry = await scenario.CreateUserAsync("sftp-henry");
        var port = await factory.PortAsync();
        using var client = await ConnectedAsync(port, "sftp-henry");
        Assert.Empty(await Run(() => client.ListDirectory("/").Where(f => f.Name is not "." and not "..").ToList()));

        using (var disabled = await scenario.Admin.PutAsync($"/api/v1/users/{henry.Id}", new UpdateUserRequest(null, null, IsDisabled: true)))
        {
            await AccessScenario.EnsureSuccessAsync(disabled);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(!client.IsConnected), "SFTP session ended");
        await Assert.ThrowsAnyAsync<Exception>(() => Run(() => client.ListDirectory("/").ToList()));
    }

    [DatabaseFact]
    public async Task AdministratorsTurnItOffAndOnAndChangeTheHint()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var viewer = await scenario.SignedInWithAsync("sftp-ivan");
        var port = await factory.PortAsync();
        using var connected = await ConnectedAsync(port, "sftp-ivan");

        var settings = (await scenario.Admin.Http.GetFromJsonAsync<SftpSettingsResponse>("/api/v1/sftp/settings", AccessScenario.Json, Ct))!;
        Assert.True(settings.Enabled);
        Assert.True(settings.Listening);
        Assert.StartsWith("SHA256:", settings.HostKeyFingerprint, StringComparison.Ordinal);
        using (var forbidden = await viewer.Http.GetAsync("/api/v1/sftp/settings", Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var invalid = await scenario.Admin.PutAsync("/api/v1/sftp/settings", new UpdateSftpSettingsRequest(true, 0, null)))
        {
            await Auth.SetupTests.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "sftp.invalid_port");
        }

        using (var invalid = await scenario.Admin.PutAsync("/api/v1/sftp/settings", new UpdateSftpSettingsRequest(true, 2022, "sftp://host:22")))
        {
            await Auth.SetupTests.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "sftp.invalid_host");
        }

        using (var off = await scenario.Admin.PutAsync("/api/v1/sftp/settings", new UpdateSftpSettingsRequest(false, 2022, "files.example.com")))
        {
            await AccessScenario.EnsureSuccessAsync(off);
        }

        // Off: the open session ends, nothing listens.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(!connected.IsConnected), "SFTP session ended");
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Services.GetRequiredService<SftpHost>().ListeningPort is null), "SFTP stopped");
        var hint = (await viewer.Http.GetFromJsonAsync<SftpConnectionResponse>("/api/v1/sftp", AccessScenario.Json, Ct))!;
        Assert.Equal((false, "files.example.com", 2022, settings.HostKeyFingerprint), (hint.Enabled, hint.Host, hint.Port, hint.HostKeyFingerprint));

        using (var on = await scenario.Admin.PutAsync("/api/v1/sftp/settings", new UpdateSftpSettingsRequest(true, 2022, null)))
        {
            await AccessScenario.EnsureSuccessAsync(on);
        }

        // On again with the same host key.
        string? fingerprint = null;
        using (var again = Client(await factory.PortAsync(), "sftp-ivan", AccessScenario.Password, fingerprint: f => fingerprint = f))
        {
            await again.ConnectAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
        }

        Assert.Equal(settings.HostKeyFingerprint, fingerprint);
        Assert.Equal(2, (await AuditAsync(AuditActions.Sftp.SettingsChanged)).Count);
    }

    /// <summary>A metadata field as text (jsonb spaces the stored JSON its own way).</summary>
    private static string? Field(AuditEntry entry, string name)
    {
        using var metadata = System.Text.Json.JsonDocument.Parse(entry.Metadata);
        return metadata.RootElement.TryGetProperty(name, out var value) ? value.ToString() : null;
    }

    private async Task<List<AuditEntry>> AuditAsync(string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking().Where(e => e.Action == action).ToListAsync(Ct);
    }
}
