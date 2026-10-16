using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;
using static Pulsar.Api.Tests.Sftp.SftpTesting;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// Parallel SFTP sign-ins on one account (docs/guides/sftp.md): each wrong password or code counts towards the lockout
/// and keeps its audit entry, and a sign-in racing a failure keeps its own.
/// </summary>
public sealed class SftpConcurrentSignInTests(SftpApiFactory factory) : IClassFixture<SftpApiFactory>
{
    // Below Sftp:MaxConnectionsPerAddress (8), so that no connection is refused before signing in.
    private const int Parallel = 7;

    [DatabaseFact]
    public async Task ParallelWrongPasswordsLockTheAccountAfterFiveAndAreEachAudited()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("sftp-race-password");
        var port = await factory.PortAsync();

        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => FailsAsync(PasswordClient(port, "sftp-race-password", "wrong " + i))));

        Assert.True(await IsLockedOutAsync("sftp-race-password"));
        var failures = await ReasonsAsync(AuditActions.Auth.LoginFailed, user.Id);
        Assert.Equal(Parallel, failures.Count);
        // The first four are wrong passwords; the fifth locks the account, and the rest find it locked.
        Assert.Equal(4, failures.Count(r => r == "invalid_credentials"));
    }

    [DatabaseFact]
    public async Task ParallelWrongCodesLockTheAccountAfterFiveAndAreEachAudited()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("sftp-race-code");
        var key = await EnableTwoFactorAsync(scenario, "sftp-race-code");
        var port = await factory.PortAsync();

        var codes = WrongCodes(key, Parallel);
        await Task.WhenAll(codes.Select(code => FailsAsync(Client(port, "sftp-race-code", AccessScenario.Password, code: code))));

        Assert.True(await IsLockedOutAsync("sftp-race-code"));
        // Each attempt is audited once: a wrong code, or the account found locked at the password or the code.
        var codeFailures = await ReasonsAsync(AuditActions.Auth.TwoFactorFailed, user.Id);
        var lockedAtPassword = (await ReasonsAsync(AuditActions.Auth.LoginFailed, user.Id)).Count(r => r == "locked_out");
        Assert.Equal(Parallel, codeFailures.Count + lockedAtPassword);
        Assert.Equal(5, codeFailures.Count(r => r == "invalid_code"));
    }

    [DatabaseFact]
    public async Task ASignInRacingAWrongPasswordKeepsItsAuditEntry()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("sftp-race-success");
        var port = await factory.PortAsync();
        const int rounds = 4;

        for (var i = 0; i < rounds; i++)
        {
            var wrong = i;
            var signIn = ConnectedAsync(port, "sftp-race-success");
            var failure = FailsAsync(PasswordClient(port, "sftp-race-success", "wrong " + wrong));
            await Task.WhenAll(signIn, failure);
            (await signIn).Dispose();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var signIns = await db.AuditLog.AsNoTracking().Where(e => e.Action == AuditActions.Auth.Login).ToListAsync(Ct);
        Assert.Equal(rounds, signIns.Count(e => e.ActorUserId?.Value == user.Id));
        Assert.Equal(rounds, (await ReasonsAsync(AuditActions.Auth.LoginFailed, user.Id)).Count);
    }

    private async Task<bool> IsLockedOutAsync(string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PulsarUser>>();
        return await users.IsLockedOutAsync((await users.FindByNameAsync(userName))!);
    }

    /// <summary>The <c>reason</c> of each entry of <paramref name="action"/> about the user.</summary>
    private async Task<List<string?>> ReasonsAsync(string action, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var target = userId.ToString();
        var metadata = await db.AuditLog.AsNoTracking().Where(e => e.Action == action && e.TargetId == target).Select(e => e.Metadata).ToListAsync(Ct);
        return [.. metadata.Select(m =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(m);
            return json.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        })];
    }

    private static async Task<string> EnableTwoFactorAsync(AccessScenario scenario, string userName)
    {
        using var user = await scenario.SignInAsync(userName);
        using var setup = await user.PostAsync("/api/v1/auth/two-factor/setup", new PasswordConfirmationRequest(AccessScenario.Password));
        await AccessScenario.EnsureSuccessAsync(setup);
        var key = (await setup.Content.ReadFromJsonAsync<TwoFactorSetupResponse>(AccessScenario.Json, Ct))!.SharedKey.Replace(" ", string.Empty, StringComparison.Ordinal);
        using var enabled = await user.PostAsync("/api/v1/auth/two-factor/enable",
            new EnableTwoFactorRequest(Totp.Compute(key, DateTimeOffset.UtcNow), AccessScenario.Password));
        await AccessScenario.EnsureSuccessAsync(enabled);
        return key;
    }

    // Six-digit codes that are none of the ones accepted now (one step of drift either way, and the next minute's too).
    private static List<string> WrongCodes(string key, int count)
    {
        var now = DateTimeOffset.UtcNow;
        var valid = new[] { -30, 0, 30, 60 }.Select(s => Totp.Compute(key, now.AddSeconds(s))).ToHashSet(StringComparer.Ordinal);
        return [.. Enumerable.Range(1, 100).Select(i => (i * 7919 % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture))
            .Where(c => !valid.Contains(c)).Take(count)];
    }
}
