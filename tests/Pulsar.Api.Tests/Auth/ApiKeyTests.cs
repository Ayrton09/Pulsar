using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Auth;

/// <summary>
/// API keys (docs/architecture/02, "API keys"): created from a session, used as <c>Bearer pulsar_…</c> by a client with
/// no cookies and no antiforgery token, limited to the permissions they list, and stopped at once.
/// </summary>
public sealed class ApiKeyTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AKeyLimitedToSomePermissionsWorksWithoutCookiesAndOnlyForThem()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-owner", Permissions.Audit.View, Permissions.User.View, Permissions.Role.Manage);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Reports", [Permissions.Audit.View, Permissions.Role.Manage], 30, AccessScenario.Password, null));
        Assert.StartsWith("pulsar_", created.Value, StringComparison.Ordinal);
        Assert.Equal(created.Value[..ApiKey.VisiblePrefixLength], created.Key.Prefix);
        using var script = Bearer(created.Value);

        using (var audit = await script.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        }

        // The user may list users, the key may not.
        using (var users = await script.GetAsync(new Uri("/api/v1/users", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        }

        // A change with no antiforgery token: there is no cookie a browser could have added.
        using (var role = await script.PostAsJsonAsync(new Uri("/api/v1/roles", UriKind.Relative),
                   new CreateRoleRequest("made-by-a-key", null, PermissionScope.Global, [Permissions.Audit.View]), Ct))
        {
            await AccessScenario.EnsureSuccessAsync(role);
        }

        // Audited as the key, on behalf of its user.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var entry = await db.AuditLog.AsNoTracking().Where(e => e.Action == AuditActions.Role.Created).OrderByDescending(e => e.OccurredAt).FirstAsync(Ct);
        Assert.Equal(AuditActorType.ApiKey, entry.ActorType);
        Assert.Equal("key-owner · Reports", entry.ActorDisplay);
        var stored = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Name == "Reports", Ct);
        Assert.NotNull(stored.LastUsedAt);
        Assert.DoesNotContain(created.Value, System.Text.Encoding.UTF8.GetString(stored.TokenHash), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task AKeyNeverReachesTheAccountTheKeysOrTheSession()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-limits", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Everything I have", null, null, AccessScenario.Password, null));
        using var script = Bearer(created.Value);

        foreach (var path in new[] { "/api/v1/account/api-keys", "/api/v1/auth/me" })
        {
            using var response = await script.GetAsync(new Uri(path, UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var create = await script.PostAsJsonAsync(new Uri("/api/v1/account/api-keys", UriKind.Relative),
                   new CreateApiKeyRequest("Child", null, null, AccessScenario.Password, null), Ct))
        {
            Assert.NotEqual(HttpStatusCode.Created, create.StatusCode);
        }

        using (var password = await script.PostAsJsonAsync(new Uri("/api/v1/auth/password", UriKind.Relative),
                   new ChangePasswordRequest(AccessScenario.Password, "another password for tests"), Ct))
        {
            Assert.NotEqual(HttpStatusCode.NoContent, password.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task ARevokedKeyADisabledUserAndAnAdministratorsRevocationStopKeysAtOnce()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-revoked", Permissions.Audit.View);
        var first = await CreateAsync(owner, new CreateApiKeyRequest("First", null, null, AccessScenario.Password, null));
        var second = await CreateAsync(owner, new CreateApiKeyRequest("Second", null, null, AccessScenario.Password, null));
        var third = await CreateAsync(owner, new CreateApiKeyRequest("Third", null, null, AccessScenario.Password, null));
        Assert.Equal(["Third", "Second", "First"], (await ListAsync(owner)).Select(k => k.Name));

        using (var revoke = await owner.DeleteAsync($"/api/v1/account/api-keys/{first.Key.Id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        await AssertStatusAsync(first.Value, HttpStatusCode.Unauthorized);
        await AssertStatusAsync(second.Value, HttpStatusCode.OK);

        // An administrator revokes all of them (a leak).
        var user = (await scenario.Admin.Http.GetFromJsonAsync<List<UserResponse>>(new Uri("/api/v1/users", UriKind.Relative), AccessScenario.Json, Ct))!
            .Single(u => u.UserName == "key-revoked");
        Assert.Equal(2, user.ApiKeys);
        using (var all = await scenario.Admin.DeleteAsync($"/api/v1/users/{user.Id}/api-keys"))
        {
            await AccessScenario.EnsureSuccessAsync(all);
            Assert.Equal(0, (await all.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!.ApiKeys);
        }

        await AssertStatusAsync(second.Value, HttpStatusCode.Unauthorized);
        await AssertStatusAsync(third.Value, HttpStatusCode.Unauthorized);

        // A disabled user's keys stop too.
        var fourth = await CreateAsync(owner, new CreateApiKeyRequest("Fourth", null, null, AccessScenario.Password, null));
        await AssertStatusAsync(fourth.Value, HttpStatusCode.OK);
        using (var disable = await scenario.Admin.PutAsync($"/api/v1/users/{user.Id}", new UpdateUserRequest(null, null, true)))
        {
            await AccessScenario.EnsureSuccessAsync(disable);
        }

        await AssertStatusAsync(fourth.Value, HttpStatusCode.Unauthorized);
        await AssertStatusAsync("pulsar_not-a-real-key-at-all-000000000000", HttpStatusCode.Unauthorized);
    }

    [DatabaseFact]
    public async Task ARequestWithAKeyActsAsTheKeyAloneWhateverCookieComesWithIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-and-cookie", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Audit only", null, null, AccessScenario.Password, null));

        // The administrator's session cookie, sent along with a key of a user who may only read the audit log.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(scenario.Admin.Http, "/api/v1/users", bearer: null));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(scenario.Admin.Http, "/api/v1/audit", created.Value));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(scenario.Admin.Http, "/api/v1/users", created.Value));

        // A key that does not work is not saved by the cookie.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(scenario.Admin.Http, "/api/v1/audit", "pulsar_not-a-real-key-at-all-000000000000"));
    }

    [DatabaseFact]
    public async Task AKeyIsMadeOnlyWithThePasswordAndGoesWhenThePasswordChanges()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-password", Permissions.Audit.View);

        // A session alone (a copied cookie) makes no key.
        using (var refused = await owner.PostAsync("/api/v1/account/api-keys", new CreateApiKeyRequest("Stolen", null, null, "not the password", null)))
        {
            await SetupTests.AssertProblemAsync(refused, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        Assert.Empty(await ListAsync(owner));

        // A mistake in the request costs no password attempt: it is refused before the password is checked.
        using (var invalid = await owner.PostAsync("/api/v1/account/api-keys", new CreateApiKeyRequest("Bad", ["no.such.permission"], null, "not the password", null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("auth.wrong_current_password", await invalid.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // Whoever had the old password may have made keys with it: changing it revokes them.
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Script", null, null, AccessScenario.Password, null));
        using var script = Bearer(created.Value);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct)).StatusCode);
        using (var changed = await owner.PostAsync("/api/v1/auth/password", new ChangePasswordRequest(AccessScenario.Password, "a brand new password for tests")))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct)).StatusCode);
        Assert.Empty(await ListAsync(owner));
    }

    [DatabaseFact]
    public async Task AnAdministratorsPasswordResetRevokesTheUsersKeys()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-reset", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Script", null, null, AccessScenario.Password, null));
        var id = (await scenario.Admin.Http.GetFromJsonAsync<List<UserResponse>>(new Uri("/api/v1/users", UriKind.Relative), AccessScenario.Json, Ct))!
            .Single(u => u.UserName == "key-reset").Id;

        using (var reset = await scenario.Admin.PostAsync($"/api/v1/users/{id}/password", new ResetPasswordRequest("an administrator's new password")))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        using var script = Bearer(created.Value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct)).StatusCode);
    }

    [DatabaseFact]
    public async Task AnExpiredKeyIsRefused()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-expired", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Expiring", null, 1, AccessScenario.Password, null));
        await AssertStatusAsync(created.Value, HttpStatusCode.OK);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
            Assert.Equal(1, await db.ApiKeys.Where(k => k.Name == "Expiring").ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, expired), Ct));
        }

        await AssertStatusAsync(created.Value, HttpStatusCode.Unauthorized);
    }

    [DatabaseFact]
    public async Task AKeyLimitedToManagingRolesGrantsOnlyWhatItIsLimitedTo()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-roles", Permissions.Role.Manage, Permissions.User.View, Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Roles", [Permissions.Role.Manage], null, AccessScenario.Password, null));
        using var script = Bearer(created.Value);

        // Its user holds user.view; the key does not, so it cannot give it either.
        using (var role = await script.PostAsJsonAsync(new Uri("/api/v1/roles", UriKind.Relative),
                   new CreateRoleRequest("escalated-by-a-key", null, PermissionScope.Global, [Permissions.User.View]), Ct))
        {
            await SetupTests.AssertProblemAsync(role, HttpStatusCode.Forbidden, "access.escalation");
        }

        using (var role = await script.PostAsJsonAsync(new Uri("/api/v1/roles", UriKind.Relative),
                   new CreateRoleRequest("managed-by-a-key", null, PermissionScope.Global, [Permissions.Role.Manage]), Ct))
        {
            await AccessScenario.EnsureSuccessAsync(role);
        }
    }

    [DatabaseFact]
    public async Task EachKeyHasItsOwnBudgetOfRequests()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Auth:ApiKeyPermitLimit", "3"));
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-budget", Permissions.Audit.View);
        var first = await CreateAsync(owner, new CreateApiKeyRequest("Busy", null, null, AccessScenario.Password, null));
        var second = await CreateAsync(owner, new CreateApiKeyRequest("Quiet", null, null, AccessScenario.Password, null));

        using var busy = Bearer(limited, first.Value);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync(busy, "/api/v1/audit", bearer: null));
        }

        using (var rejected = await busy.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct))
        {
            await SetupTests.AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
        }

        using var quiet = Bearer(limited, second.Value);
        Assert.Equal(HttpStatusCode.OK, await SendAsync(quiet, "/api/v1/audit", bearer: null));
    }

    [DatabaseFact]
    public async Task RefusedRequestsWithAKeyCountToo()
    {
        // The prober's first request is a lookup of its address too (its key was not known yet): one of the four.
        using var limited = factory.WithWebHostBuilder(b => b
            .UseSetting("RateLimiting:Auth:ApiKeyPermitLimit", "3")
            .UseSetting("RateLimiting:Auth:ApiKeyLookupPermitLimit", "4"));
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-refused-budget", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Prober", null, null, AccessScenario.Password, null));

        // Each refused request still costs the panel a key, a user and their permissions: it is counted.
        using var prober = Bearer(limited, created.Value);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(prober, "/api/v1/users", bearer: null));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(prober, "/api/v1/audit", bearer: null));

        // Guessing keys: counted by the client's address.
        using var guesser = Bearer(limited, "pulsar_not-a-real-key-at-all-000000000000");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(guesser, "/api/v1/audit", bearer: null));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(guesser, "/api/v1/audit", "pulsar_another-made-up-key-000000000000000"));
    }

    [DatabaseFact]
    public async Task GuessesOverTheAddressBudgetNeverReachTheKeysAndKeysInUseKeepTheirOwn()
    {
        var keyQueries = new QueryCounter("api_keys");
        using var limited = factory.WithWebHostBuilder(b => b
            .UseSetting("RateLimiting:Auth:ApiKeyLookupPermitLimit", "3")
            .ConfigureTestServices(services => services.ConfigureDbContext<PulsarDbContext>(options => options.AddInterceptors(keyQueries))));
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-guessed-near", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("In use", null, null, AccessScenario.Password, null));

        // A script in use behind the same address as the guesser (one household, one office): its first request is
        // one of the address's lookups.
        using var script = Bearer(limited, created.Value);
        Assert.Equal(HttpStatusCode.OK, await SendAsync(script, "/api/v1/audit", bearer: null));

        using var guesser = Bearer(limited, "pulsar_not-a-real-key-at-all-000000000000");
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(guesser, "/api/v1/audit", $"pulsar_made-up-key-number-{i}-0000000000000000"));
        }

        var looked = keyQueries.Count;
        Assert.True(looked >= 3, $"each lookup reads the keys ({looked} reads)");
        for (var i = 0; i < 5; i++)
        {
            using var rejected = await guesser.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct);
            await SetupTests.AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
            Assert.True(rejected.Headers.RetryAfter is not null);
        }

        Assert.Equal(looked, keyQueries.Count);

        // The key in use is known: its own budget, whatever was guessed from its address.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(script, "/api/v1/audit", bearer: null));

        // Revoked: no longer known, so again one of the address's lookups, which are used up.
        using (var revoked = await owner.DeleteAsync($"/api/v1/account/api-keys/{created.Key.Id}"))
        {
            await AccessScenario.EnsureSuccessAsync(revoked);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(script, "/api/v1/audit", bearer: null));
        var afterRevoked = keyQueries.Count;
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync(script, "/api/v1/audit", bearer: null));
        Assert.Equal(afterRevoked, keyQueries.Count);
    }

    [DatabaseFact]
    public async Task CredentialConfirmationBeforeAPasswordResetCannotIssueAKeyAfterIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var user = await scenario.CreateUserAsync("key-reset-between-check-and-save");
        var hook = new BeforeUserLock(async () =>
        {
            using var reset = await scenario.Admin.PostAsync($"/api/v1/users/{user.Id}/password",
                new ResetPasswordRequest("a replacement password for this test"));
            await AccessScenario.EnsureSuccessAsync(reset);
        });
        using var hooked = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<PulsarDbContext>(options => options.AddInterceptors(hook))));
        using var owner = await PanelClient.CreateAsync(hooked);
        using (var login = await owner.LoginAsync("key-reset-between-check-and-save", AccessScenario.Password))
        {
            await AccessScenario.EnsureSuccessAsync(login);
        }

        await owner.GetSessionAsync();
        using var refused = await owner.PostAsync("/api/v1/account/api-keys",
            new CreateApiKeyRequest("Must not be created", null, null, AccessScenario.Password, null));
        Assert.True(hook.Fired);
        await SetupTests.AssertProblemAsync(refused, HttpStatusCode.BadRequest, "auth.credentials_changed");
        await using var scope = factory.Services.CreateAsyncScope();
        var userId = new UserId(user.Id);
        Assert.False(await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().ApiKeys
            .AnyAsync(k => k.UserId == userId, Ct));
    }

    /// <summary>Runs once before the lock, so the resetting request can complete without waiting on that lock.</summary>
    private sealed class BeforeUserLock(Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => Volatile.Read(ref _fired) != 0;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT 1 FROM users WHERE id =", StringComparison.Ordinal)
                && command.CommandText.EndsWith("FOR UPDATE", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await action().WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Counts the commands the panel sends whose text names a table.</summary>
    private sealed class QueryCounter(string table) : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Counted(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Counted(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Counted(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Counted(DbCommand command)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string path, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private async Task AssertStatusAsync(string key, HttpStatusCode expected)
    {
        using var client = Bearer(key);
        using var response = await client.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct);
        Assert.Equal(expected, response.StatusCode);
    }

    private HttpClient Bearer(string key) => Bearer(factory, key);

    private static HttpClient Bearer(WebApplicationFactory<Program> app, string key)
    {
        var client = app.CreateDefaultClient(new Uri("https://localhost"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task<CreatedApiKey> CreateAsync(PanelClient client, CreateApiKeyRequest request)
    {
        using var response = await client.PostAsync("/api/v1/account/api-keys", request);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKey>(AccessScenario.Json, Ct))!;
    }

    private static async Task<List<ApiKeyView>> ListAsync(PanelClient client) =>
        (await client.Http.GetFromJsonAsync<List<ApiKeyView>>(new Uri("/api/v1/account/api-keys", UriKind.Relative), AccessScenario.Json, Ct))!;
}
