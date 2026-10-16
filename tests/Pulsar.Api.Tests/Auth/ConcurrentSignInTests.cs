using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Auth;

/// <summary>
/// Parallel sign-in attempts on one account (docs/architecture/08, "Users and web"): each wrong password or code counts
/// towards the lockout and keeps its audit entry, and a sign-in racing a failure keeps its own.
/// </summary>
public sealed class ConcurrentSignInTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const int Parallel = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ParallelWrongPasswordsLockTheAccountAfterFiveAndAreEachAudited()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("race-password");
        var clients = await ClientsAsync(Parallel);

        var answers = await Task.WhenAll(clients.Select((c, i) => StatusAsync(c.LoginAsync("race-password", "wrong password " + i))));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a));
        Assert.True(await IsLockedOutAsync("race-password"));
        var failures = await ReasonsAsync(AuditActions.Auth.LoginFailed, user.Id);
        Assert.Equal(Parallel, failures.Count);
        // The first four are wrong passwords; the fifth locks the account, and the rest find it locked.
        Assert.Equal(4, failures.Count(r => r == "invalid_credentials"));
        clients.ForEach(c => c.Dispose());
    }

    [DatabaseFact]
    public async Task ASignInRacingAWrongPasswordKeepsItsAuditEntry()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("race-success");
        const int rounds = 6;

        for (var i = 0; i < rounds; i++)
        {
            var clients = await ClientsAsync(2);
            var answers = await Task.WhenAll(
                StatusAsync(clients[0].LoginAsync("race-success", AccessScenario.Password)),
                StatusAsync(clients[1].LoginAsync("race-success", "wrong password " + i)));
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], answers);
            clients.ForEach(c => c.Dispose());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var signIns = await db.AuditLog.AsNoTracking().Where(e => e.Action == AuditActions.Auth.Login).ToListAsync(Ct);
        Assert.Equal(rounds, signIns.Count(e => e.ActorUserId?.Value == user.Id));
        Assert.Equal(rounds, (await ReasonsAsync(AuditActions.Auth.LoginFailed, user.Id)).Count);
    }

    [DatabaseFact]
    public async Task ParallelWrongCodesLockTheAccountAfterFiveAndAreEachAudited()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("race-code");
        var key = await EnableTwoFactorAsync(scenario, "race-code");
        using var client = await PanelClient.CreateAsync(factory);
        using (var password = await client.LoginAsync("race-code", AccessScenario.Password))
        {
            await AccessScenario.EnsureSuccessAsync(password);
        }

        var codes = WrongCodes(key, Parallel);
        var answers = await Task.WhenAll(codes.Select(code => StatusAsync(client.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(code, null)))));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Unauthorized, a));
        Assert.True(await IsLockedOutAsync("race-code"));
        var failures = await ReasonsAsync(AuditActions.Auth.TwoFactorFailed, user.Id);
        Assert.Equal(Parallel, failures.Count);
        // The first four are wrong codes; the fifth locks the account, and the rest find it locked.
        Assert.Equal(4, failures.Count(r => r == "invalid_code"));
    }

    private async Task<List<PanelClient>> ClientsAsync(int count)
    {
        var clients = new List<PanelClient>();
        for (var i = 0; i < count; i++)
        {
            clients.Add(await PanelClient.CreateAsync(factory));
        }

        return clients;
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
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
