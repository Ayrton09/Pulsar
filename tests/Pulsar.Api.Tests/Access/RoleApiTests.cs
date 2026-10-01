using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Tests.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Access;

public sealed class RoleApiTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task RoleLifecycleIsAudited()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var role = await scenario.CreateRoleAsync("Operators", Permissions.Server.Start, Permissions.Server.Stop);

        using (var updated = await admin.Http.PutAsJsonAsync(new Uri($"/api/v1/roles/{role.Id}", UriKind.Relative),
            new UpdateRoleRequest("Operators", "Start and view", [Permissions.Server.Start, Permissions.Server.View]),
            TestContext.Current.CancellationToken))
        {
            await AccessScenario.EnsureSuccessAsync(updated);
            var body = await updated.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, TestContext.Current.CancellationToken);
            Assert.Equal([Permissions.Server.Start, Permissions.Server.View], body!.Permissions);
        }

        using (var deleted = await admin.Http.DeleteAsync(new Uri($"/api/v1/roles/{role.Id}", UriKind.Relative), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var target = role.Id.ToString();
        var entries = await db.AuditLog.Where(e => e.TargetId == target).OrderBy(e => e.OccurredAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([AuditActions.Role.Created, AuditActions.Role.Updated, AuditActions.Role.Deleted], entries.Select(e => e.Action));
        using var diff = JsonDocument.Parse(entries[1].Metadata);
        Assert.Equal(["server.stop"], diff.RootElement.GetProperty("removed").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["server.view"], diff.RootElement.GetProperty("added").EnumerateArray().Select(e => e.GetString()));
    }

    [DatabaseFact]
    public async Task NamesAreUniqueIgnoringCase()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateRoleAsync("Moderators", Permissions.Server.ConsoleView);

        using var duplicate = await admin.PostAsync("/api/v1/roles",
            new CreateRoleRequest("moderators", null, PermissionScope.Global, []));

        await SetupTests.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "role.name_taken");
    }

    [DatabaseFact]
    public async Task AdministratorRoleCannotBeChangedOrDeleted()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var roles = await admin.Http.GetFromJsonAsync<RoleSummary[]>(new Uri("/api/v1/roles", UriKind.Relative), AccessScenario.Json, TestContext.Current.CancellationToken);
        var administrator = Assert.Single(roles!, r => r.IsSystem);
        Assert.Equal(PermissionCatalog.AllKeys.Order(StringComparer.Ordinal), administrator.Permissions);

        using (var update = await admin.Http.PutAsJsonAsync(new Uri($"/api/v1/roles/{administrator.Id}", UriKind.Relative),
            new UpdateRoleRequest("Admins", null, []), TestContext.Current.CancellationToken))
        {
            await SetupTests.AssertProblemAsync(update, HttpStatusCode.Conflict, "role.system_immutable");
        }

        using var delete = await admin.Http.DeleteAsync(new Uri($"/api/v1/roles/{administrator.Id}", UriKind.Relative), TestContext.Current.CancellationToken);
        await SetupTests.AssertProblemAsync(delete, HttpStatusCode.Conflict, "role.system_immutable");
    }

    [DatabaseFact]
    public async Task RolesInUseCannotBeDeleted()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var role = await scenario.CreateRoleAsync("In use", Permissions.Server.View);
        await scenario.CreateUserAsync("role-holder", role);

        using var delete = await admin.Http.DeleteAsync(new Uri($"/api/v1/roles/{role.Id}", UriKind.Relative), TestContext.Current.CancellationToken);

        await SetupTests.AssertProblemAsync(delete, HttpStatusCode.Conflict, "role.in_use");
    }

    [DatabaseFact]
    public async Task RoleManagersCanOnlyUsePermissionsTheyHold()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var manager = await scenario.SignedInWithAsync("limited-role-manager", Permissions.Role.Manage, Permissions.Server.View);

        using (var allowed = await manager.PostAsync("/api/v1/roles",
            new CreateRoleRequest("Viewers", null, PermissionScope.Server, [Permissions.Server.View])))
        {
            Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        }

        using var escalation = await manager.PostAsync("/api/v1/roles",
            new CreateRoleRequest("Killers", null, PermissionScope.Server, [Permissions.Server.Kill]));
        await SetupTests.AssertProblemAsync(escalation, HttpStatusCode.Forbidden, "access.escalation");
    }

    [DatabaseFact]
    public async Task InvalidRolesAreRejected()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;

        using (var notGrantable = await admin.PostAsync("/api/v1/roles",
            new CreateRoleRequest("Bad", null, PermissionScope.Server, [Permissions.User.Manage])))
        {
            await SetupTests.AssertProblemAsync(notGrantable, HttpStatusCode.BadRequest, "role.permission_not_grantable");
        }

        using var unknown = await admin.PostAsync("/api/v1/roles", new CreateRoleRequest("Bad", null, PermissionScope.Global, ["server.fly"]));
        await SetupTests.AssertProblemAsync(unknown, HttpStatusCode.BadRequest, "role.permission_unknown");
    }
}
