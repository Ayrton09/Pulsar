using System.Net;
using System.Net.Http.Json;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Access;

namespace Pulsar.Api.Tests.Access;

public sealed class UserApiTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task CreatedUserSignsInWithTheirRolePermissions()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var role = await scenario.CreateRoleAsync("Console", Permissions.Server.ConsoleView, Permissions.Server.ConsoleWrite);
        var created = await scenario.CreateUserAsync("console-user", role);
        Assert.Equal(["Console"], created.Roles.Select(r => r.Name));

        using var user = await scenario.SignInAsync("console-user");
        var me = await user.Http.GetFromJsonAsync<CurrentUserResponse>(new Uri("/api/v1/auth/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal([Permissions.Server.ConsoleView, Permissions.Server.ConsoleWrite], me!.Permissions);
    }

    [DatabaseFact]
    public async Task RoleChangesApplyToTheNextRequest()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var viewer = await scenario.CreateRoleAsync("Audit viewers", Permissions.Audit.View);
        var user = await scenario.CreateUserAsync("promoted-user");
        using var client = await scenario.SignInAsync("promoted-user");
        Assert.Empty((await client.GetSessionAsync()).User!.Permissions);

        using var set = await admin.Http.PutAsJsonAsync(new Uri($"/api/v1/users/{user.Id}/roles", UriKind.Relative),
            new SetUserRolesRequest([viewer.Id]), TestContext.Current.CancellationToken);
        await AccessScenario.EnsureSuccessAsync(set);

        Assert.Equal([Permissions.Audit.View], (await client.GetSessionAsync()).User!.Permissions);
    }

    [DatabaseFact]
    public async Task UserManagersCannotGrantTheAdministratorRoleOrTouchAdministrators()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var roles = await admin.Http.GetFromJsonAsync<Pulsar.Application.Access.RoleSummary[]>(
            new Uri("/api/v1/roles", UriKind.Relative), AccessScenario.Json, TestContext.Current.CancellationToken);
        var administratorRole = roles!.Single(r => r.IsSystem);
        var target = await scenario.CreateUserAsync("ordinary-user");
        var me = (await admin.GetSessionAsync()).User!;
        using var manager = await scenario.SignedInWithAsync("user-manager", Permissions.User.Manage, Permissions.User.View);

        using (var grantAdmin = await manager.Http.PutAsJsonAsync(new Uri($"/api/v1/users/{target.Id}/roles", UriKind.Relative),
            new SetUserRolesRequest([administratorRole.Id]), TestContext.Current.CancellationToken))
        {
            await SetupTests.AssertProblemAsync(grantAdmin, HttpStatusCode.Forbidden, "access.escalation");
        }

        using (var disableAdmin = await manager.Http.PutAsJsonAsync(new Uri($"/api/v1/users/{me.Id}", UriKind.Relative),
            new UpdateUserRequest(null, null, IsDisabled: true), TestContext.Current.CancellationToken))
        {
            await SetupTests.AssertProblemAsync(disableAdmin, HttpStatusCode.Forbidden, "access.escalation");
        }

        using var resetAdmin = await manager.PostAsync($"/api/v1/users/{me.Id}/password", new ResetPasswordRequest("taking over the admin"));
        await SetupTests.AssertProblemAsync(resetAdmin, HttpStatusCode.Forbidden, "access.escalation");
    }

    [DatabaseFact]
    public async Task NobodyChangesTheirOwnRolesOrDeletesThemselves()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var me = (await admin.GetSessionAsync()).User!;

        using (var ownRoles = await admin.Http.PutAsJsonAsync(new Uri($"/api/v1/users/{me.Id}/roles", UriKind.Relative),
            new SetUserRolesRequest([]), TestContext.Current.CancellationToken))
        {
            await SetupTests.AssertProblemAsync(ownRoles, HttpStatusCode.Forbidden, "access.self_change");
        }

        using var deleteSelf = await admin.Http.DeleteAsync(new Uri($"/api/v1/users/{me.Id}", UriKind.Relative), TestContext.Current.CancellationToken);
        await SetupTests.AssertProblemAsync(deleteSelf, HttpStatusCode.Forbidden, "access.self_change");
    }

    [DatabaseFact]
    public async Task DisablingAUserEndsTheirAccess()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("soon-disabled");

        using var disable = await admin.Http.PutAsJsonAsync(new Uri($"/api/v1/users/{user.Id}", UriKind.Relative),
            new UpdateUserRequest("Soon Disabled", null, IsDisabled: true), TestContext.Current.CancellationToken);
        await AccessScenario.EnsureSuccessAsync(disable);

        using var client = await PanelClient.CreateAsync(factory);
        using var login = await client.LoginAsync("soon-disabled", AccessScenario.Password);
        await SetupTests.AssertProblemAsync(login, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [DatabaseFact]
    public async Task PasswordResetReplacesTheOldPassword()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("forgetful-user");

        using var reset = await admin.PostAsync($"/api/v1/users/{user.Id}/password", new ResetPasswordRequest("a brand new password"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var client = await PanelClient.CreateAsync(factory);
        using (var oldPassword = await client.LoginAsync("forgetful-user", AccessScenario.Password))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        }

        using var newPassword = await client.LoginAsync("forgetful-user", "a brand new password");
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [DatabaseFact]
    public async Task DeletedUsersDisappear()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("short-lived");

        using var delete = await admin.Http.DeleteAsync(new Uri($"/api/v1/users/{user.Id}", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var get = await admin.GetAsync($"/api/v1/users/{user.Id}");
        await SetupTests.AssertProblemAsync(get, HttpStatusCode.NotFound, "user.not_found");
    }
}
