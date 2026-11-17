using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Servers;

public sealed class ServerRenameTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task APanelNameChangesWhileTheGameRunsAndWhileItsNodeIsDisconnected()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "rename-running");
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "Original panel name");
        var id = created.Server.Id;
        await ServerApi.WaitForAsync(admin, id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        await ServerApi.CommandAsync(admin, id, "start");
        var running = (await ServerApi.WaitForAsync(admin, id, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running")).Server;
        var starts = agent.Processes.Started.Count;
        var marker = Path.Combine(agent.ServersRoot, running.ShortId, "rename-marker.txt");
        await File.WriteAllTextAsync(marker, "same files", Ct);

        var renamed = await RenameAsync(admin, id, "  New panel name  ");

        Assert.Equal("New panel name", renamed.Server.Name);
        Assert.Equal((running.Id, running.ShortId, running.NodeId, running.TemplateId, running.CreatedAt, running.LastStartedAt),
            (renamed.Server.Id, renamed.Server.ShortId, renamed.Server.NodeId, renamed.Server.TemplateId, renamed.Server.CreatedAt, renamed.Server.LastStartedAt));
        Assert.Equal(ServerRuntimeState.Running, renamed.Server.RuntimeState);
        Assert.Equal(starts, agent.Processes.Started.Count);
        Assert.Equal("same files", await File.ReadAllTextAsync(marker, Ct));

        await agent.StopAsync();
        await InProcessAgent.EventuallyAsync(async () => !(await NodeApi.GetAsync(admin, running.NodeId)).Node.Connected, "node disconnected");
        Assert.Equal("Offline panel name", (await RenameAsync(admin, id, "Offline panel name")).Server.Name);
    }

    [DatabaseFact]
    public async Task OnlyTheSettingsEditorOfThatServerRenamesItAndInvalidNamesChangeNothing()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "rename-permissions");
        var first = (await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "First rename target")).Server.Id;
        var other = (await ServerApi.CreateAsync(admin, agent.Config.NodeId, "Other rename target")).Server.Id;
        using var startup = await scenario.SignedInWithAsync("rename-startup-only", Permissions.Server.View, Permissions.Server.StartupEdit);
        using (var refused = await startup.PutAsync($"/api/v1/servers/{first}/name", new RenameServerRequest("Not allowed")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var view = await scenario.CreateRoleAsync("rename-view", Permissions.Server.View);
        var user = await scenario.CreateUserAsync("rename-scoped-editor", view);
        using var roleResponse = await admin.PostAsync("/api/v1/roles",
            new CreateRoleRequest("rename-one-server", null, PermissionScope.Server, [Permissions.Server.SettingsEdit]));
        await AccessScenario.EnsureSuccessAsync(roleResponse);
        var role = (await roleResponse.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, Ct))!;
        using var grant = await admin.PutAsync($"/api/v1/users/{user.Id}/servers/{first}/roles/{role.Id}", new { });
        await AccessScenario.EnsureSuccessAsync(grant);
        using var editor = await scenario.SignInAsync("rename-scoped-editor");
        Assert.Equal("Changed once", (await RenameAsync(editor, first, "Changed once")).Server.Name);
        using (var refused = await editor.PutAsync($"/api/v1/servers/{other}/name", new RenameServerRequest("Not their server")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        foreach (var name in new[] { "", "   ", new string('x', 65), "Two\nLines", "Left\u202eRight" })
        {
            using var invalid = await editor.PutAsync($"/api/v1/servers/{first}/name", new RenameServerRequest(name));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        await RenameAsync(editor, first, " Changed once ");
        Assert.Equal("Changed once", (await ServerApi.GetAsync(admin, first))!.Server.Name);
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(first) && e.Action == AuditActions.Server.Renamed).ToListAsync(Ct);
        Assert.Equal(user.Id, Assert.Single(audit).ActorUserId?.Value);
    }

    private static async Task<ServerDetails> RenameAsync(PanelClient client, Guid server, string name)
    {
        using var response = await client.PutAsync($"/api/v1/servers/{server}/name", new RenameServerRequest(name));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
    }

    [DatabaseFact]
    public async Task AConcurrentStateReportDoesNotLoseTheNameOrDuplicateItsAuditEvent()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "rename-race");
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, "Before the rename race");
        var id = new ServerId(created.Server.Id);
        await ServerApi.WaitForAsync(admin, id.Value, s => s.Server.ActiveOperation is null, "initial operations completed");
        var raced = false;
        var hook = new DatabaseHook();
        hook.BeforeSave(c => c.ChangeTracker.Entries<AuditEntry>().Any(e => e.Entity.Action == AuditActions.Server.Renamed), async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().Servers.Where(s => s.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.UpdatedAt, DateTimeOffset.UtcNow), Ct);
            raced = true;
        });
        using var app = factory.WithDatabaseHook(hook);
        using var client = await PanelClient.CreateAsync(app);
        using (var login = await client.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword))
        {
            await AccessScenario.EnsureSuccessAsync(login);
        }
        await client.GetSessionAsync();

        Assert.Equal("After the rename race", (await RenameAsync(client, id.Value, "After the rename race")).Server.Name);
        Assert.True(raced);
        await using var check = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog
            .CountAsync(e => e.ServerId == id && e.Action == AuditActions.Server.Renamed, Ct));
    }
}
