using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    private async Task AssertStatusAsync(string key, HttpStatusCode expected)
    {
        using var client = Bearer(key);
        using var response = await client.GetAsync(new Uri("/api/v1/audit", UriKind.Relative), Ct);
        Assert.Equal(expected, response.StatusCode);
    }

    private HttpClient Bearer(string key)
    {
        var client = factory.CreateDefaultClient(new Uri("https://localhost"));
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
