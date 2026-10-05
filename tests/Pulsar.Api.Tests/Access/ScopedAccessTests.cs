using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Realtime;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Access;

/// <summary>
/// Roles given on one node or one server (docs/architecture/02): a user sees and acts on that node or server only,
/// is told others do not exist, and loses access at once when the role is taken away.
/// </summary>
public sealed class ScopedAccessTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ARoleOnOneServerOpensThatServerOnly()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "one-server");
        var mine = await InstalledAsync(scenario.Admin, agent.Config!.NodeId, "Mine");
        var theirs = (await ServerApi.CreateAsync(scenario.Admin, agent.Config.NodeId, "Theirs")).Server.Id;
        var role = await CreateRoleAsync(scenario, "srv-operator", PermissionScope.Server,
            Permissions.Server.View, Permissions.Server.Start, Permissions.Server.Stop, Permissions.Server.ConsoleView);
        var friend = await scenario.CreateUserAsync("server-friend");
        await GrantAsync(scenario.Admin, friend.Id, "servers", mine, role.Id);
        using var client = await scenario.SignInAsync("server-friend");

        var list = await ListAsync<ServerSummary>(client, "/api/v1/servers");
        Assert.Equal([mine], list.Select(s => s.Id));
        Assert.NotNull(await ServerApi.GetAsync(client, mine));
        Assert.Null(await ServerApi.GetAsync(client, theirs)); // 404: as if it did not exist
        await AssertCodeAsync(client.PostAsync($"/api/v1/servers/{theirs}/start"), HttpStatusCode.NotFound, "server.not_found");
        await AssertCodeAsync(client.GetAsync($"/api/v1/servers/{theirs}/files?path="), HttpStatusCode.NotFound, "server.not_found");
        await AssertStatusAsync(client.PostAsync($"/api/v1/servers/{mine}/install"), HttpStatusCode.Forbidden); // seen, not allowed
        await AssertStatusAsync(client.DeleteAsync($"/api/v1/servers/{mine}"), HttpStatusCode.Forbidden);
        await AssertStatusAsync(client.GetAsync($"/api/v1/servers/{mine}/files?path="), HttpStatusCode.Forbidden);
        await AssertStatusAsync(client.GetAsync("/api/v1/nodes"), HttpStatusCode.Forbidden);
        await AssertStatusAsync(client.GetAsync($"/api/v1/nodes/{agent.Config.NodeId}"), HttpStatusCode.Forbidden);
        await AssertStatusAsync(client.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden);
        using (var started = await client.PostAsync($"/api/v1/servers/{mine}/start"))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        var session = await client.GetSessionAsync();
        Assert.Empty(session.User!.Permissions);
        var granted = Assert.Single(session.User.ServerPermissions);
        Assert.Equal(mine, granted.Id);
        Assert.Contains(Permissions.Server.Start, granted.Permissions);
        Assert.Empty(session.User.NodePermissions);
    }

    [DatabaseFact]
    public async Task ARoleOnANodeOpensItsServersAndCreatingThere()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "node-a");
        await using var other = await ConnectedAgentAsync(scenario.Admin, "node-b");
        var onNode = (await ServerApi.CreateAsync(scenario.Admin, agent.Config!.NodeId, "On node")).Server.Id;
        var elsewhere = (await ServerApi.CreateAsync(scenario.Admin, other.Config!.NodeId, "Elsewhere")).Server.Id;
        var role = await CreateRoleAsync(scenario, "node-host", PermissionScope.Node, Permissions.Server.View, Permissions.Server.Create);
        var host = await scenario.CreateUserAsync("node-host-user");
        await GrantAsync(scenario.Admin, host.Id, "nodes", agent.Config.NodeId, role.Id);
        using var client = await scenario.SignInAsync("node-host-user");

        Assert.Equal([onNode], (await ListAsync<ServerSummary>(client, "/api/v1/servers")).Select(s => s.Id));
        Assert.Equal([agent.Config.NodeId], (await ListAsync<NodeSummary>(client, "/api/v1/nodes")).Select(n => n.Id)); // to create servers on it
        Assert.Null(await ServerApi.GetAsync(client, elsewhere));
        var created = await ServerApi.CreateAsync(client, agent.Config.NodeId, "Made by the host");
        Assert.Contains(created.Server.Id, (await ListAsync<ServerSummary>(client, "/api/v1/servers")).Select(s => s.Id)); // a new server on the node
        var template = await ServerApi.TestTemplateAsync(client);
        await AssertCodeAsync(client.PostAsync("/api/v1/servers", new CreateServerRequest(other.Config.NodeId, template.Id, "Not there", null)),
            HttpStatusCode.NotFound, "node.not_found");
    }

    [DatabaseFact]
    public async Task TakingTheRoleAwayClosesTheLiveChannelAndTheServer()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "revoke");
        var mine = await InstalledAsync(scenario.Admin, agent.Config!.NodeId, "Revoked");
        var theirs = await InstalledAsync(scenario.Admin, agent.Config.NodeId, "Not mine");
        var role = await CreateRoleAsync(scenario, "srv-viewer", PermissionScope.Server, Permissions.Server.View);
        var friend = await scenario.CreateUserAsync("revoked-friend");
        await GrantAsync(scenario.Admin, friend.Id, "servers", mine, role.Id);
        using var client = await scenario.SignInAsync("revoked-friend");
        await using var browser = await BrowserHub.ConnectAsync(factory, client);
        Assert.True(await browser.Connection.InvokeAsync<bool>("WatchServers", Ct));
        Assert.NotNull(await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", mine, Ct));
        Assert.Null(await browser.Connection.InvokeAsync<ServerWatch?>("WatchServer", theirs, Ct));

        // Changes are told about the server they see, not about the other one.
        await ServerApi.CommandAsync(scenario.Admin, theirs, "start");
        await ServerApi.CommandAsync(scenario.Admin, mine, "start");
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(browser.ServersChanged.Contains(mine)), "change pushed");
        await ServerApi.WaitForAsync(scenario.Admin, theirs, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        Assert.DoesNotContain(theirs, browser.ServersChanged);

        var closed = new TaskCompletionSource();
        browser.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        using (var revoked = await scenario.Admin.DeleteAsync($"/api/v1/users/{friend.Id}/servers/{mine}/roles/{role.Id}"))
        {
            await AccessScenario.EnsureSuccessAsync(revoked);
            var user = (await revoked.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!;
            Assert.Empty(user.Access);
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // With no server left to see, server routes are closed to them altogether (whether the server exists or not).
        await AssertStatusAsync(client.GetAsync($"/api/v1/servers/{mine}"), HttpStatusCode.Forbidden);
        await AssertStatusAsync(client.GetAsync("/api/v1/servers"), HttpStatusCode.Forbidden);
    }

    [DatabaseFact]
    public async Task ScopedGrantsFollowTheSameRules()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "rules");
        var server = (await ServerApi.CreateAsync(scenario.Admin, agent.Config!.NodeId, "Rules")).Server.Id;
        var serverRole = await CreateRoleAsync(scenario, "srv-rules", PermissionScope.Server, Permissions.Server.View, Permissions.Server.Kill);
        var nodeRole = await CreateRoleAsync(scenario, "node-rules", PermissionScope.Node, Permissions.Server.View);
        var target = await scenario.CreateUserAsync("rules-target");

        // A user manager may give only what they hold there.
        using var manager = await scenario.SignedInWithAsync("rules-manager", Permissions.User.Manage, Permissions.Server.View);
        await AssertCodeAsync(manager.PutAsync($"/api/v1/users/{target.Id}/servers/{server}/roles/{serverRole.Id}", new { }),
            HttpStatusCode.Forbidden, "access.escalation");
        // A role goes only where its scope says, and nobody changes their own.
        await AssertCodeAsync(scenario.Admin.PutAsync($"/api/v1/users/{target.Id}/servers/{server}/roles/{nodeRole.Id}", new { }),
            HttpStatusCode.BadRequest, "access.scope_mismatch");
        var admin = (await scenario.Admin.GetSessionAsync()).User!;
        await AssertCodeAsync(scenario.Admin.PutAsync($"/api/v1/users/{admin.Id}/servers/{server}/roles/{serverRole.Id}", new { }),
            HttpStatusCode.Forbidden, "access.self_change");
        await AssertCodeAsync(scenario.Admin.PutAsync($"/api/v1/users/{target.Id}/servers/{Guid.NewGuid()}/roles/{serverRole.Id}", new { }),
            HttpStatusCode.NotFound, "server.not_found");

        // Granting twice changes nothing; an assigned role cannot be deleted, and counts its user once.
        await GrantAsync(scenario.Admin, target.Id, "servers", server, serverRole.Id);
        var user = await GrantAsync(scenario.Admin, target.Id, "servers", server, serverRole.Id);
        var access = Assert.Single(user.Access);
        Assert.Equal((PermissionScope.Server, server, "Rules", "srv-rules"), (access.Scope, access.TargetId, access.TargetName, access.Role.Name));
        using (var roles = await scenario.Admin.GetAsync($"/api/v1/roles/{serverRole.Id}"))
        {
            Assert.Equal(1, (await roles.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, Ct))!.AssignedUsers);
        }

        await AssertCodeAsync(scenario.Admin.DeleteAsync($"/api/v1/roles/{serverRole.Id}"), HttpStatusCode.Conflict, "role.in_use");
    }

    private static async Task<Guid> InstalledAsync(PanelClient admin, Guid node, string name)
    {
        var created = await ServerApi.CreateAsync(admin, node, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }

    private static async Task<RoleSummary> CreateRoleAsync(AccessScenario scenario, string name, PermissionScope scope, params string[] permissions)
    {
        using var response = await scenario.Admin.PostAsync("/api/v1/roles", new CreateRoleRequest(name, null, scope, permissions));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, Ct))!;
    }

    private static async Task<UserResponse> GrantAsync(PanelClient admin, Guid user, string kind, Guid target, Guid role)
    {
        using var response = await admin.PutAsync($"/api/v1/users/{user}/{kind}/{target}/roles/{role}", new { });
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!;
    }

    private static async Task<IReadOnlyList<T>> ListAsync<T>(PanelClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(AccessScenario.Json, Ct))!;
    }

    private static async Task AssertStatusAsync(Task<HttpResponseMessage> sending, HttpStatusCode status)
    {
        using var response = await sending;
        Assert.Equal(status, response.StatusCode);
    }

    private static async Task AssertCodeAsync(Task<HttpResponseMessage> sending, HttpStatusCode status, string code)
    {
        using var response = await sending;
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status && body.Contains($"\"{code}\"", StringComparison.Ordinal), $"{(int)response.StatusCode}: {body}");
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"scoped-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
