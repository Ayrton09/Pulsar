using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Auth;

/// <summary>A panel whose clock the test moves, so authenticator codes can be computed and replayed on purpose.</summary>
public sealed class TwoFactorApiFactory : PulsarApiFactory
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
        });
    }
}

/// <summary>TOTP two-factor authentication (docs/architecture/08, "Users and web").</summary>
public sealed class TwoFactorTests(TwoFactorApiFactory factory) : IClassFixture<TwoFactorApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task EnablingTwoFactorTakesACodeAndThenSigningInAsksForOne()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("totp-user");
        using var user = await scenario.SignInAsync("totp-user");

        var status = await GetAsync<TwoFactorStatusResponse>(user, "/api/v1/auth/two-factor");
        Assert.False(status.Enabled);

        // A stolen session cannot replace the key (and end the owner's other sessions): setting up takes the password.
        using (var wrongPassword = await user.PostAsync("/api/v1/auth/two-factor/setup", new PasswordConfirmationRequest("not my password")))
        {
            await SetupTests.AssertProblemAsync(wrongPassword, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        var setup = await SetupAsync(user);
        Assert.StartsWith("otpauth://totp/Pulsar:totp-user?secret=", setup.AuthenticatorUri, StringComparison.Ordinal);
        Assert.Contains("&issuer=Pulsar", setup.AuthenticatorUri, StringComparison.Ordinal);
        var key = setup.SharedKey.Replace(" ", string.Empty, StringComparison.Ordinal);

        using (var wrong = await user.PostAsync("/api/v1/auth/two-factor/enable", new EnableTwoFactorRequest(WrongCode(key), AccessScenario.Password)))
        {
            await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.invalid_code");
        }

        var enableCode = Code(key);

        // A stolen session cannot turn it on: that takes the password too.
        using (var wrongPassword = await user.PostAsync("/api/v1/auth/two-factor/enable", new EnableTwoFactorRequest(enableCode, "not my password")))
        {
            await SetupTests.AssertProblemAsync(wrongPassword, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        Assert.False((await GetAsync<TwoFactorStatusResponse>(user, "/api/v1/auth/two-factor")).Enabled);

        using var enabled = await user.PostAsync("/api/v1/auth/two-factor/enable", new EnableTwoFactorRequest(enableCode, AccessScenario.Password));
        await AccessScenario.EnsureSuccessAsync(enabled);
        var codes = (await enabled.Content.ReadFromJsonAsync<RecoveryCodesResponse>(Ct))!.RecoveryCodes;
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct(StringComparer.Ordinal).Count());

        // This session goes on; the user now has two-factor authentication.
        var me = (await user.GetSessionAsync()).User!;
        Assert.True(me.TwoFactorEnabled);

        using var other = await PanelClient.CreateAsync(factory);
        using (var password = await other.LoginAsync("totp-user", AccessScenario.Password))
        {
            await AccessScenario.EnsureSuccessAsync(password);
            var login = (await password.Content.ReadFromJsonAsync<LoginResponse>(Ct))!;
            Assert.True(login.TwoFactorRequired);
            Assert.Null(login.User);
        }

        using (var notYet = await other.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, notYet.StatusCode);
        }

        // The code that enabled it was used: an authenticator code works once.
        using (var replayed = await other.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(enableCode, null)))
        {
            await SetupTests.AssertProblemAsync(replayed, HttpStatusCode.Unauthorized, "auth.invalid_code");
        }

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using (var second = await other.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(Code(key), null)))
        {
            await AccessScenario.EnsureSuccessAsync(second);
            Assert.Equal("totp-user", (await second.Content.ReadFromJsonAsync<CurrentUserResponse>(Ct))!.UserName);
        }

        await other.GetSessionAsync();
        using (var signedIn = await other.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        var actions = await AuditActionsForAsync("totp-user");
        Assert.Contains(AuditActions.Auth.TwoFactorEnabled, actions);
        Assert.Contains(AuditActions.Auth.TwoFactorFailed, actions);
    }

    [DatabaseFact]
    public async Task ARecoveryCodeWorksOnceAndNothingIsStoredInClear()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("recovery-user");
        var (key, codes) = await EnableAsync(scenario, "recovery-user");

        using (var first = await SignInWithRecoveryCodeAsync("recovery-user", codes[0].ToLowerInvariant()))
        {
            await AccessScenario.EnsureSuccessAsync(first);
        }

        using (var again = await SignInWithRecoveryCodeAsync("recovery-user", codes[0]))
        {
            await SetupTests.AssertProblemAsync(again, HttpStatusCode.Unauthorized, "auth.invalid_code");
        }

        using var user = await SignInWithCodeAsync("recovery-user", key);
        Assert.Equal(9, (await GetAsync<TwoFactorStatusResponse>(user, "/api/v1/auth/two-factor")).RecoveryCodesLeft);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var id = (await db.Users.SingleAsync(u => u.UserName == "recovery-user", Ct)).Id;
        var stored = await db.UserTokens.Where(t => t.UserId == id).Select(t => t.Value).ToListAsync(Ct);
        Assert.NotEmpty(stored);
        Assert.All(stored, value =>
        {
            Assert.DoesNotContain(key, value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.All(codes, code => Assert.DoesNotContain(code, value ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        });

        Assert.Contains(AuditActions.Auth.RecoveryCodeUsed, await AuditActionsForAsync("recovery-user"));
    }

    [DatabaseFact]
    public async Task DisablingAndNewRecoveryCodesNeedThePassword()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("disable-user");
        var (key, codes) = await EnableAsync(scenario, "disable-user");
        using var user = await SignInWithCodeAsync("disable-user", key);

        using (var wrong = await user.PostAsync("/api/v1/auth/two-factor/recovery-codes", new PasswordConfirmationRequest("not my password")))
        {
            await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        using (var renewed = await user.PostAsync("/api/v1/auth/two-factor/recovery-codes", new PasswordConfirmationRequest(AccessScenario.Password)))
        {
            await AccessScenario.EnsureSuccessAsync(renewed);
            var fresh = (await renewed.Content.ReadFromJsonAsync<RecoveryCodesResponse>(Ct))!.RecoveryCodes;
            Assert.Equal(10, fresh.Count);
            Assert.Empty(fresh.Intersect(codes, StringComparer.Ordinal));
        }

        using (var oldCode = await SignInWithRecoveryCodeAsync("disable-user", codes[0]))
        {
            await SetupTests.AssertProblemAsync(oldCode, HttpStatusCode.Unauthorized, "auth.invalid_code");
        }

        using (var wrong = await user.PostAsync("/api/v1/auth/two-factor/disable", new PasswordConfirmationRequest("not my password")))
        {
            await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        using (var disabled = await user.PostAsync("/api/v1/auth/two-factor/disable", new PasswordConfirmationRequest(AccessScenario.Password)))
        {
            Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        }

        using var plain = await PanelClient.CreateAsync(factory);
        using var login = await plain.LoginAsync("disable-user", AccessScenario.Password);
        Assert.False((await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.TwoFactorRequired);
        Assert.Contains(AuditActions.Auth.TwoFactorDisabled, await AuditActionsForAsync("disable-user"));
    }

    [DatabaseFact]
    public async Task WrongCodesLockTheAccount()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("guessed-user");
        var (key, _) = await EnableAsync(scenario, "guessed-user");

        using var client = await PanelClient.CreateAsync(factory);
        using (var password = await client.LoginAsync("guessed-user", AccessScenario.Password))
        {
            await AccessScenario.EnsureSuccessAsync(password);
        }

        for (var i = 0; i < 5; i++)
        {
            using var guess = await client.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(WrongCode(key, i), null));
            Assert.Equal(HttpStatusCode.Unauthorized, guess.StatusCode);
        }

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using var right = await client.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(Code(key), null));
        await SetupTests.AssertProblemAsync(right, HttpStatusCode.Unauthorized, "auth.invalid_code");
    }

    [DatabaseFact]
    public async Task TheSecondStepNeedsTheFirst()
    {
        using var admin = await factory.CreateAdminClientAsync();
        using var client = await PanelClient.CreateAsync(factory);

        using var response = await client.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest("123456", null));

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "auth.invalid_code");
    }

    [DatabaseFact]
    public async Task ARoleCanRequireTwoFactorAuthentication()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var role = await scenario.CreateRoleAsync("strict-role", Permissions.Server.View);
        await scenario.CreateUserAsync("strict-user", role);

        using (var required = await admin.PutAsync($"/api/v1/roles/{role.Id}/two-factor", new SetRoleTwoFactorRequest(true)))
        {
            await AccessScenario.EnsureSuccessAsync(required);
            Assert.True((await required.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, Ct))!.RequiresTwoFactor);
        }

        using var user = await scenario.SignInAsync("strict-user");
        Assert.True((await user.GetSessionAsync()).User!.TwoFactorSetupRequired);

        // Until the user sets it up, only the session, logout and setting it up are open to them.
        using (var blocked = await user.GetAsync("/api/v1/servers"))
        {
            await SetupTests.AssertProblemAsync(blocked, HttpStatusCode.Forbidden, "auth.two_factor_required");
        }

        using (var me = await user.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        var setup = await SetupAsync(user);
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using (var enabled = await user.PostAsync("/api/v1/auth/two-factor/enable", new EnableTwoFactorRequest(Code(setup.SharedKey), AccessScenario.Password)))
        {
            await AccessScenario.EnsureSuccessAsync(enabled);
        }

        await user.GetSessionAsync();
        using (var allowed = await user.GetAsync("/api/v1/servers"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // While a role requires it, the user cannot turn it off.
        using (var disable = await user.PostAsync("/api/v1/auth/two-factor/disable", new PasswordConfirmationRequest(AccessScenario.Password)))
        {
            await SetupTests.AssertProblemAsync(disable, HttpStatusCode.Conflict, "auth.two_factor_required_by_role");
        }
    }

    [DatabaseFact]
    public async Task OnlyWhoMayDefineARoleChangesItsRequirement()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        using var roleManager = await scenario.SignedInWithAsync("role-manager-2fa", Permissions.Role.Manage);
        var administrator = (await GetAsync<RoleSummary[]>(admin, "/api/v1/roles")).Single(r => r.IsSystem);

        using (var escalation = await roleManager.PutAsync($"/api/v1/roles/{administrator.Id}/two-factor", new SetRoleTwoFactorRequest(true)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, escalation.StatusCode);
        }

        using (var missing = await admin.PutAsync($"/api/v1/roles/{Guid.NewGuid()}/two-factor", new SetRoleTwoFactorRequest(true)))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task AnAdministratorResetsAUsersTwoFactorAuthentication()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var created = await scenario.CreateUserAsync("lost-phone-user");
        var (key, _) = await EnableAsync(scenario, "lost-phone-user");
        using var session = await SignInWithCodeAsync("lost-phone-user", key);
        Assert.True((await GetAsync<UserResponse>(admin, $"/api/v1/users/{created.Id}")).TwoFactorEnabled);

        using (var reset = await admin.PostAsync($"/api/v1/users/{created.Id}/two-factor/reset"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        Assert.False((await GetAsync<UserResponse>(admin, $"/api/v1/users/{created.Id}")).TwoFactorEnabled);
        using (var ended = await session.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        using var plain = await PanelClient.CreateAsync(factory);
        using var login = await plain.LoginAsync("lost-phone-user", AccessScenario.Password);
        Assert.False((await login.Content.ReadFromJsonAsync<LoginResponse>(Ct))!.TwoFactorRequired);
        Assert.Contains(AuditActions.User.TwoFactorReset, await AuditActionsAsync());
    }

    private async Task<(string Key, IReadOnlyList<string> Codes)> EnableAsync(AccessScenario scenario, string userName)
    {
        using var user = await scenario.SignInAsync(userName);
        var setup = await SetupAsync(user);
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using var enabled = await user.PostAsync("/api/v1/auth/two-factor/enable", new EnableTwoFactorRequest(Code(setup.SharedKey), AccessScenario.Password));
        await AccessScenario.EnsureSuccessAsync(enabled);
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        return (setup.SharedKey, (await enabled.Content.ReadFromJsonAsync<RecoveryCodesResponse>(Ct))!.RecoveryCodes);
    }

    private async Task<PanelClient> SignInWithCodeAsync(string userName, string key)
    {
        var client = await PanelClient.CreateAsync(factory);
        using var password = await client.LoginAsync(userName, AccessScenario.Password);
        await AccessScenario.EnsureSuccessAsync(password);
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using var code = await client.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(Code(key), null));
        await AccessScenario.EnsureSuccessAsync(code);
        await client.GetSessionAsync();
        return client;
    }

    private async Task<HttpResponseMessage> SignInWithRecoveryCodeAsync(string userName, string recoveryCode)
    {
        using var client = await PanelClient.CreateAsync(factory);
        using var password = await client.LoginAsync(userName, AccessScenario.Password);
        await AccessScenario.EnsureSuccessAsync(password);
        return await client.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(null, recoveryCode));
    }

    private string Code(string key) => Totp.Compute(key, factory.Clock.GetUtcNow());

    // A six-digit code that is not the current one (nor the ones next to it, which are accepted for clock drift).
    private string WrongCode(string key, int offset = 0)
    {
        var now = factory.Clock.GetUtcNow();
        var valid = new[] { -30, 0, 30 }.Select(s => Totp.Compute(key, now.AddSeconds(s))).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(offset, 100).Select(i => (i * 7919 % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture))
            .First(c => !valid.Contains(c));
    }

    private static async Task<T> GetAsync<T>(PanelClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    private static async Task<TwoFactorSetupResponse> SetupAsync(PanelClient client)
    {
        using var response = await client.PostAsync("/api/v1/auth/two-factor/setup", new PasswordConfirmationRequest(AccessScenario.Password));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<TwoFactorSetupResponse>(AccessScenario.Json, Ct))!;
    }

    private async Task<List<string>> AuditActionsForAsync(string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var id = (await db.Users.SingleAsync(u => u.UserName == userName, Ct)).Id.ToString();
        return await db.AuditLog.Where(e => e.TargetId == id || e.ActorDisplay == userName).Select(e => e.Action).ToListAsync(Ct);
    }

    private async Task<List<string>> AuditActionsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        return await db.AuditLog.Select(e => e.Action).ToListAsync(Ct);
    }
}
