using System.Net;
using System.Net.Http.Json;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Tests.Access;

/// <summary>
/// Endpoint × principal matrix: who gets past authorization on every user/role endpoint. Allowed principals may still
/// get 400/404 (empty bodies, unknown ids); what matters here is that nobody else gets past 401/403.
/// </summary>
public sealed class AuthorizationMatrixTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const string Anonymous = "anonymous";
    private const string NoPermissions = "no-permissions";
    private const string UserViewer = "user-viewer";
    private const string RoleManager = "role-manager";
    private const string Administrator = "administrator";

    private static readonly string s_someId = Guid.CreateVersion7().ToString();

    public static TheoryData<string, string, string[]> Endpoints => new()
    {
        { "GET", "/api/v1/auth/me", [NoPermissions, UserViewer, RoleManager, Administrator] },
        { "POST", "/api/v1/auth/logout", [NoPermissions, UserViewer, RoleManager, Administrator] },
        { "GET", "/api/v1/permissions", [NoPermissions, UserViewer, RoleManager, Administrator] },
        { "PUT", "/api/v1/auth/profile", [NoPermissions, UserViewer, RoleManager, Administrator] },
        { "POST", "/api/v1/auth/password", [NoPermissions, UserViewer, RoleManager, Administrator] },
        { "GET", "/api/v1/users", [UserViewer, Administrator] },
        { "GET", $"/api/v1/users/{s_someId}", [UserViewer, Administrator] },
        { "POST", "/api/v1/users", [Administrator] },
        { "PUT", $"/api/v1/users/{s_someId}", [Administrator] },
        { "POST", $"/api/v1/users/{s_someId}/password", [Administrator] },
        { "PUT", $"/api/v1/users/{s_someId}/roles", [Administrator] },
        { "DELETE", $"/api/v1/users/{s_someId}", [Administrator] },
        { "GET", "/api/v1/roles", [UserViewer, RoleManager, Administrator] },
        { "GET", $"/api/v1/roles/{s_someId}", [UserViewer, RoleManager, Administrator] },
        { "POST", "/api/v1/roles", [RoleManager, Administrator] },
        { "PUT", $"/api/v1/roles/{s_someId}", [RoleManager, Administrator] },
        { "DELETE", $"/api/v1/roles/{s_someId}", [RoleManager, Administrator] },
        { "GET", "/api/v1/audit", [Administrator] },
        { "GET", "/api/v1/nodes", [Administrator] },
        { "GET", $"/api/v1/nodes/{s_someId}", [Administrator] },
        { "POST", "/api/v1/nodes", [Administrator] },
        { "PUT", $"/api/v1/nodes/{s_someId}", [Administrator] },
        { "POST", $"/api/v1/nodes/{s_someId}/disable", [Administrator] },
        { "POST", $"/api/v1/nodes/{s_someId}/enable", [Administrator] },
        { "POST", $"/api/v1/nodes/{s_someId}/ping", [Administrator] },
        { "DELETE", $"/api/v1/nodes/{s_someId}", [Administrator] },
        { "POST", $"/api/v1/nodes/{s_someId}/enrollment-tokens", [Administrator] },
        { "POST", $"/api/v1/nodes/{s_someId}/credentials/{s_someId}/revoke", [Administrator] },
        { "GET", "/api/v1/sftp", [NoPermissions, UserViewer, RoleManager, Administrator] },
        { "GET", "/api/v1/sftp/settings", [Administrator] },
        { "PUT", "/api/v1/sftp/settings", [Administrator] },
        { "GET", "/api/v1/appearance", [NoPermissions, UserViewer, RoleManager, Administrator] },
        { "PUT", "/api/v1/appearance", [Administrator] },
        { "GET", "/api/v1/public-ip-certificate", [Administrator] },
        { "PUT", "/api/v1/public-ip-certificate", [Administrator] },
        { "POST", "/api/v1/public-ip-certificate/renew", [Administrator] },
    };

    [DatabaseTheory]
    [MemberData(nameof(Endpoints))]
    public async Task OnlyAllowedPrincipalsGetPastAuthorization(string method, string path, string[] allowed)
    {
        var clients = await CreateClientsAsync();
        try
        {
            foreach (var (principal, client) in clients)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
                if (method is "POST" or "PUT")
                {
                    request.Content = JsonContent.Create(new { });
                }

                using var response = await client.Http.SendAsync(request, TestContext.Current.CancellationToken);
                var status = response.StatusCode;
                if (allowed.Contains(principal))
                {
                    Assert.True(status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                        $"{principal} should be allowed on {method} {path} but got {(int)status}.");
                }
                else
                {
                    var expected = principal == Anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
                    Assert.True(status == expected, $"{principal} on {method} {path}: expected {(int)expected}, got {(int)status}.");
                }
            }
        }
        finally
        {
            foreach (var (_, client) in clients)
            {
                client.Dispose();
            }
        }
    }

    // Users are created once per database; every theory row signs them in again (logout rows end sessions).
    private async Task<List<(string Principal, PanelClient Client)>> CreateClientsAsync()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await factory.OnceAsync("matrix-users", async () =>
        {
            await scenario.CreateUserAsync(NoPermissions);
            await scenario.CreateUserAsync(UserViewer, await scenario.CreateRoleAsync("matrix-viewer", Permissions.User.View));
            await scenario.CreateUserAsync(RoleManager, await scenario.CreateRoleAsync("matrix-roles", Permissions.Role.Manage));
        });
        return
        [
            (Anonymous, await PanelClient.CreateAsync(factory)),
            (NoPermissions, await scenario.SignInAsync(NoPermissions)),
            (UserViewer, await scenario.SignInAsync(UserViewer)),
            (RoleManager, await scenario.SignInAsync(RoleManager)),
            (Administrator, scenario.Admin),
        ];
    }

}
