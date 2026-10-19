using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Api.Tests.Sftp;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Auth;

/// <summary>
/// SSH keys on the account (docs/guides/sftp.md, "SSH keys"): added with the password and, with two-factor
/// authentication on, the authenticator code (never a recovery code), checked before either is asked for, at most ten
/// per user and each once, listed and deleted by their owner only, audited, never reached with an API key, and all
/// deleted at once by an administrator.
/// </summary>
public sealed class SshKeyApiTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AddingAKeyTakesThePasswordAndListsItWithItsFingerprint()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var uma = await scenario.CreateUserAsync("ssh-uma");
        using var account = await scenario.SignInAsync("ssh-uma");
        using var key = TestSshKey.Ed25519Key("uma@laptop");

        // A wrong password: refused, counted towards the lockout, audited.
        using (var wrong = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest(null, key.Line, "not my password", null)))
        {
            await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        Assert.Equal(1, (await UserAsync("ssh-uma")).AccessFailedCount);
        Assert.Contains(await AuditAsync(AuditActions.Auth.SshKeyAdded), e => e.Outcome == AuditOutcome.Failure && e.TargetId == uma.Id.ToString());

        // Without a name, the key's comment names it.
        var added = await key.AddAsync(account);
        Assert.Equal(("uma@laptop", SshPublicKey.Ed25519, 256, key.Fingerprint), (added.Name, added.Type, added.Bits, added.Fingerprint));
        Assert.Null(added.LastUsedAt);
        var listed = (await account.Http.GetFromJsonAsync<List<SshKeyView>>("/api/v1/account/ssh-keys", AccessScenario.Json, Ct))!;
        Assert.Equal([added.Id], listed.Select(k => k.Id));
        Assert.Contains(await AuditAsync(AuditActions.Auth.SshKeyAdded), e => e.Outcome == AuditOutcome.Success && e.ActorUserId?.Value == uma.Id
            && e.Metadata!.Contains(key.Fingerprint, StringComparison.Ordinal));

        // What is wrong with the key is said first: no password checked, nothing counted.
        using var privateKey = RSA.Create(2048);
        foreach (var (text, code) in new[]
                 {
                     (key.Line, "ssh_key.duplicate"),
                     ("ssh-ed25519 AAAA", "ssh_key.invalid"),
                     ("ssh-dss AAAAB3NzaC1kc3MAAACBAKKMW3h4X1RGEy4R1vFF test", "ssh_key.unsupported_type"),
                     (privateKey.ExportPkcs8PrivateKeyPem(), "ssh_key.private_key"),
                     ("---- BEGIN SSH2 PUBLIC KEY ----\nAAAA\n---- END SSH2 PUBLIC KEY ----", "ssh_key.wrong_format"),
                 })
        {
            using var refused = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest("Name", text, "not my password", null));
            await SetupTests.AssertProblemAsync(refused, code == "ssh_key.duplicate" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, code);
        }

        using var weak = TestSshKey.Rsa(1024);
        using (var refused = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest("Old", weak.Line, AccessScenario.Password, null)))
        {
            await SetupTests.AssertProblemAsync(refused, HttpStatusCode.BadRequest, "ssh_key.rsa_too_short");
        }

        using var nameless = TestSshKey.Ecdsa(ECCurve.NamedCurves.nistP256, comment: "");
        using (var refused = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest(null, nameless.Line, AccessScenario.Password, null)))
        {
            await SetupTests.AssertProblemAsync(refused, HttpStatusCode.BadRequest, "ssh_key.name_invalid");
        }

        Assert.Equal(1, (await UserAsync("ssh-uma")).AccessFailedCount);
    }

    [DatabaseFact]
    public async Task ATwoFactorUserAlsoGivesTheCodeOfTheirApp()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var vera = await scenario.CreateUserAsync("ssh-vera");
        var (secret, recoveryCodes) = await SftpTesting.EnableTwoFactorAsync(scenario, "ssh-vera");
        using var account = await SftpTesting.SignInWithCodeAsync(factory, "ssh-vera", secret);
        using var key = TestSshKey.Ed25519Key();

        // No code, a recovery code, a wrong code: refused; the guesses count towards the lockout.
        using (var none = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest("Laptop", key.Line, AccessScenario.Password, null)))
        {
            await SetupTests.AssertProblemAsync(none, HttpStatusCode.BadRequest, "auth.invalid_code");
        }

        Assert.Equal(0, (await UserAsync("ssh-vera")).AccessFailedCount);
        var current = Totp.Compute(secret, DateTimeOffset.UtcNow.AddSeconds(30));
        foreach (var code in new[] { recoveryCodes[0], current == "000000" ? "111111" : "000000" })
        {
            using var wrong = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest("Laptop", key.Line, AccessScenario.Password, code));
            await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.invalid_code");
        }

        Assert.Equal(2, (await UserAsync("ssh-vera")).AccessFailedCount);
        Assert.Contains(await AuditAsync(AuditActions.Auth.SshKeyAdded), e => e.Outcome == AuditOutcome.Failure && e.TargetId == vera.Id.ToString()
            && e.Metadata!.Contains("invalid_code", StringComparison.Ordinal));
        Assert.Empty((await account.Http.GetFromJsonAsync<List<SshKeyView>>("/api/v1/account/ssh-keys", AccessScenario.Json, Ct))!);

        // The code of the app: added; the same code again is not taken a second time.
        var added = await key.AddAsync(account, "Laptop", current);
        Assert.Equal("Laptop", added.Name);
        using var another = TestSshKey.Ed25519Key();
        using (var replayed = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest("Desktop", another.Line, AccessScenario.Password, current)))
        {
            await SetupTests.AssertProblemAsync(replayed, HttpStatusCode.BadRequest, "auth.invalid_code");
        }
    }

    [DatabaseFact]
    public async Task AUserHasAtMostTenKeysAndOnlyTheirOwn()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var walt = await scenario.CreateUserAsync("ssh-walt");
        await scenario.CreateUserAsync("ssh-xena");
        using var account = await scenario.SignInAsync("ssh-walt");
        using var other = await scenario.SignInAsync("ssh-xena");
        var keys = Enumerable.Range(0, SshKey.MaxPerUser + 1).Select(i => TestSshKey.Ed25519Key($"key-{i}")).ToList();
        try
        {
            var added = new List<SshKeyView>();
            foreach (var key in keys.Take(SshKey.MaxPerUser))
            {
                added.Add(await key.AddAsync(account));
            }

            using (var tooMany = await account.PostAsync("/api/v1/account/ssh-keys", new AddSshKeyRequest(null, keys[^1].Line, AccessScenario.Password, null)))
            {
                await SetupTests.AssertProblemAsync(tooMany, HttpStatusCode.Conflict, "ssh_key.too_many");
            }

            // Another user sees none of them, cannot delete one, and may add the same key to their own account.
            Assert.Empty((await other.Http.GetFromJsonAsync<List<SshKeyView>>("/api/v1/account/ssh-keys", AccessScenario.Json, Ct))!);
            using (var theirs = await other.DeleteAsync($"/api/v1/account/ssh-keys/{added[0].Id}"))
            {
                await SetupTests.AssertProblemAsync(theirs, HttpStatusCode.NotFound, "ssh_key.not_found");
            }

            Assert.Equal(keys[0].Fingerprint, (await keys[0].AddAsync(other)).Fingerprint);

            // Deleted by its owner: audited, gone, and room for another.
            using (var deleted = await account.DeleteAsync($"/api/v1/account/ssh-keys/{added[0].Id}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }

            using (var again = await account.DeleteAsync($"/api/v1/account/ssh-keys/{added[0].Id}"))
            {
                await SetupTests.AssertProblemAsync(again, HttpStatusCode.NotFound, "ssh_key.not_found");
            }

            Assert.Contains(await AuditAsync(AuditActions.Auth.SshKeyDeleted), e => e.ActorUserId?.Value == walt.Id
                && e.Metadata!.Contains(keys[0].Fingerprint, StringComparison.Ordinal));
            await keys[^1].AddAsync(account);
            Assert.Equal(SshKey.MaxPerUser, (await account.Http.GetFromJsonAsync<List<SshKeyView>>("/api/v1/account/ssh-keys", AccessScenario.Json, Ct))!.Count);
        }
        finally
        {
            keys.ForEach(k => k.Dispose());
        }
    }

    [DatabaseFact]
    public async Task KeysAreNeverReachedWithAnApiKeyAndAnAdministratorDeletesThemAll()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var account = await scenario.SignedInWithAsync("ssh-yuri", Permissions.Audit.View);
        using var key = TestSshKey.Rsa(2048);
        var added = await key.AddAsync(account, "Workstation");

        using (var created = await account.PostAsync("/api/v1/account/api-keys", new CreateApiKeyRequest("Script", null, null)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            var value = (await created.Content.ReadFromJsonAsync<CreatedApiKey>(AccessScenario.Json, Ct))!.Value;
            using var script = factory.CreateDefaultClient(new Uri("https://localhost"));
            script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
            using (var list = await script.GetAsync(new Uri("/api/v1/account/ssh-keys", UriKind.Relative), Ct))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
            }

            using var scripted = TestSshKey.Ed25519Key();
            using (var add = await script.PostAsJsonAsync(new Uri("/api/v1/account/ssh-keys", UriKind.Relative),
                       new AddSshKeyRequest("By a script", scripted.Line, AccessScenario.Password, null), Ct))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, add.StatusCode);
            }

            using (var delete = await script.DeleteAsync(new Uri($"/api/v1/account/ssh-keys/{added.Id}", UriKind.Relative), Ct))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
            }
        }

        var user = (await scenario.Admin.Http.GetFromJsonAsync<List<UserResponse>>(new Uri("/api/v1/users", UriKind.Relative), AccessScenario.Json, Ct))!
            .Single(u => u.UserName == "ssh-yuri");
        Assert.Equal(1, user.SshKeys);
        using (var all = await scenario.Admin.DeleteAsync($"/api/v1/users/{user.Id}/ssh-keys"))
        {
            await AccessScenario.EnsureSuccessAsync(all);
            Assert.Equal(0, (await all.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!.SshKeys);
        }

        Assert.Empty((await account.Http.GetFromJsonAsync<List<SshKeyView>>("/api/v1/account/ssh-keys", AccessScenario.Json, Ct))!);
        Assert.Contains(await AuditAsync(AuditActions.User.SshKeysDeleted), e => e.TargetId == user.Id.ToString()
            && e.Metadata!.Contains(key.Fingerprint, StringComparison.Ordinal));

        // A deleted user's keys go with them.
        await key.AddAsync(account, "Again");
        using (var deleted = await scenario.Admin.DeleteAsync($"/api/v1/users/{user.Id}"))
        {
            await AccessScenario.EnsureSuccessAsync(deleted);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().SshKeys.AnyAsync(k => k.UserId == new Pulsar.Domain.Common.UserId(user.Id), Ct));
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
