using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulsar.Api.Access;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>Builds users and roles through the public API, as an administrator would.</summary>
public sealed class AccessScenario(PulsarApiFactory factory, PanelClient admin)
{
    public const string Password = "a password for tests";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<AccessScenario> CreateAsync(PulsarApiFactory factory) =>
        new(factory, await factory.CreateAdminClientAsync());

    public PanelClient Admin => admin;

    public async Task<RoleSummary> CreateRoleAsync(string name, params string[] permissions)
    {
        using var response = await admin.PostAsync("/api/v1/roles", new CreateRoleRequest(name, null, PermissionScope.Global, permissions));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RoleSummary>(Json, TestContext.Current.CancellationToken))!;
    }

    public async Task<UserResponse> CreateUserAsync(string userName, params RoleSummary[] roles)
    {
        using var response = await admin.PostAsync("/api/v1/users",
            new CreateUserRequest(userName, Password, null, null, [.. roles.Select(r => r.Id)]));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json, TestContext.Current.CancellationToken))!;
    }

    /// <summary>Creates a user with a role holding <paramref name="permissions"/> and returns a client signed in as them.</summary>
    public async Task<PanelClient> SignedInWithAsync(string userName, params string[] permissions)
    {
        var roles = permissions.Length == 0 ? [] : new[] { await CreateRoleAsync(userName + "-role", permissions) };
        await CreateUserAsync(userName, roles);
        return await SignInAsync(userName);
    }

    public async Task<PanelClient> SignInAsync(string userName, string password = Password)
    {
        var client = await PanelClient.CreateAsync(factory);
        using var login = await client.LoginAsync(userName, password);
        await EnsureSuccessAsync(login);
        await client.GetSessionAsync();
        return client;
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }
    }
}
