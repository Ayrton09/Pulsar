using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
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
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Reports", [Permissions.Audit.View, Permissions.Role.Manage], 30));
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
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Everything I have", null, null));
        using var script = Bearer(created.Value);

        foreach (var path in new[] { "/api/v1/account/api-keys", "/api/v1/auth/me" })
        {
            using var response = await script.GetAsync(new Uri(path, UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var create = await script.PostAsJsonAsync(new Uri("/api/v1/account/api-keys", UriKind.Relative),
                   new CreateApiKeyRequest("Child", null, null), Ct))
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
        var first = await CreateAsync(owner, new CreateApiKeyRequest("First", null, null));
        var second = await CreateAsync(owner, new CreateApiKeyRequest("Second", null, null));
        var third = await CreateAsync(owner, new CreateApiKeyRequest("Third", null, null));
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
        var fourth = await CreateAsync(owner, new CreateApiKeyRequest("Fourth", null, null));
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
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Audit only", null, null));

        // The administrator's session cookie, sent along with a key of a user who may only read the audit log.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(scenario.Admin.Http, "/api/v1/users", bearer: null));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(scenario.Admin.Http, "/api/v1/audit", created.Value));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(scenario.Admin.Http, "/api/v1/users", created.Value));

        // A key that does not work is not saved by the cookie.
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(scenario.Admin.Http, "/api/v1/audit", "pulsar_not-a-real-key-at-all-000000000000"));
    }

    [DatabaseFact]
    public async Task AnExpiredKeyIsRefused()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-expired", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Expiring", null, 1));
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
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Roles", [Permissions.Role.Manage], null));
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
        var first = await CreateAsync(owner, new CreateApiKeyRequest("Busy", null, null));
        var second = await CreateAsync(owner, new CreateApiKeyRequest("Quiet", null, null));

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
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Auth:ApiKeyPermitLimit", "3"));
        var scenario = await AccessScenario.CreateAsync(factory);
        using var owner = await scenario.SignedInWithAsync("key-refused-budget", Permissions.Audit.View);
        var created = await CreateAsync(owner, new CreateApiKeyRequest("Prober", null, null));

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
