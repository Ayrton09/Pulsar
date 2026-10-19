using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Nodes;
using Pulsar.Api.Realtime;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Api.Tests.Servers;
using Pulsar.Application.Access;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Nodes;
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
    public async Task CreatingServersOnANodeShowsItsNameAndStateOnly()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "slim");
        var node = agent.Config!.NodeId;
        using (var described = await scenario.Admin.PutAsync($"/api/v1/nodes/{node}", new UpdateNodeRequest("scoped-slim", "Rack 4", "play.example.com")))
        {
            await AccessScenario.EnsureSuccessAsync(described);
        }

        var creatorRole = await CreateRoleAsync(scenario, "slim-creator", PermissionScope.Node, Permissions.Server.Create);
        var viewerRole = await CreateRoleAsync(scenario, "slim-viewer", PermissionScope.Node, Permissions.Node.View);
        var creator = await scenario.CreateUserAsync("slim-creator-user");
        var viewer = await scenario.CreateUserAsync("slim-viewer-user");
        await GrantAsync(scenario.Admin, creator.Id, "nodes", node, creatorRole.Id);
        await GrantAsync(scenario.Admin, viewer.Id, "nodes", node, viewerRole.Id);
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(scenario.Admin, node)).Node.Metrics is not null, "usage reported");
        using var creatorClient = await scenario.SignInAsync("slim-creator-user");
        using var viewerClient = await scenario.SignInAsync("slim-viewer-user");
        using var everywhere = await scenario.SignedInWithAsync("slim-global-creator", Permissions.Server.Create);

        // Whoever sees the node gets all of it; creating servers there (on it or everywhere) gets its name and state.
        foreach (var client in new[] { scenario.Admin, viewerClient })
        {
            var full = (await ListAsync<NodeSummary>(client, "/api/v1/nodes")).Single(n => n.Id == node);
            Assert.Equal(("Rack 4", "play.example.com"), (full.Description, full.PublicHost));
            Assert.NotNull(full.Inventory);
            Assert.NotNull(full.Metrics);
            Assert.NotNull(full.LastSeenAt);
            Assert.NotNull(full.CredentialFingerprint);
            Assert.Equal(PanelKeyTrust.Current, full.PanelKeyTrust);
        }

        foreach (var client in new[] { creatorClient, everywhere })
        {
            var listed = await ListAsync<NodeSummary>(client, "/api/v1/nodes");
            var slim = listed.Single(n => n.Id == node);
            Assert.Equal(("scoped-slim", NodeStatus.Active, true), (slim.Name, slim.Status, slim.Connected));
            Assert.All(listed, n => Assert.Equal(PanelKeyTrust.Unknown, n.PanelKeyTrust));
            Assert.All(listed, n => Assert.Equal((null, null, null, null, null, null, null, null, null),
                (n.Description, n.PublicHost, n.LastSeenAt, n.Inventory, n.Metrics, n.EnrollmentTokenName, n.CredentialFingerprint,
                    n.ConnectedWithCertificate, n.AgentCanUseCertificate)));
        }
    }

    [DatabaseFact]
    public async Task AnApiKeyLimitedToCreatingServersGetsTheNodesNameAndStateOnly()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "key-slim");
        var node = agent.Config!.NodeId;
        using (var described = await scenario.Admin.PutAsync($"/api/v1/nodes/{node}", new UpdateNodeRequest("scoped-key-slim", "Rack 4", "play.example.com")))
        {
            await AccessScenario.EnsureSuccessAsync(described);
        }

        using (var allocated = await scenario.Admin.PostAsync($"/api/v1/nodes/{node}/allocations",
                   new CreateAllocationsRequest("0.0.0.0", "27300-27301", "Forwarded on the rack 4 router")))
        {
            await AccessScenario.EnsureSuccessAsync(allocated);
        }

        // The user holds node.view and server.create on the node.
        var role = await CreateRoleAsync(scenario, "key-host", PermissionScope.Node, Permissions.Node.View, Permissions.Server.Create);
        var user = await scenario.CreateUserAsync("key-host-user");
        await GrantAsync(scenario.Admin, user.Id, "nodes", node, role.Id);
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(scenario.Admin, node)).Node.Metrics is not null, "usage reported");
        using var client = await scenario.SignInAsync("key-host-user");
        var everything = await CreateKeyAsync(client, "Everything I have", null);
        var creating = await CreateKeyAsync(client, "Servers only", [Permissions.Server.Create]);
        using var unlimited = Bearer(everything.Value);
        using var limited = Bearer(creating.Value);

        // Signed in, or with a key that keeps node.view, they see all of the node and its notes.
        foreach (var http in new[] { client.Http, unlimited })
        {
            var full = (await ListAsync<NodeSummary>(http, "/api/v1/nodes")).Single(n => n.Id == node);
            Assert.Equal(("Rack 4", "play.example.com"), (full.Description, full.PublicHost));
            Assert.NotNull(full.Inventory);
            Assert.NotNull(full.Metrics);
            Assert.NotNull(full.CredentialFingerprint);
            Assert.Equal(PanelKeyTrust.Current, full.PanelKeyTrust);
            Assert.All(await ListAsync<AllocationView>(http, $"/api/v1/nodes/{node}/allocations"),
                a => Assert.Equal("Forwarded on the rack 4 router", a.Notes));
        }

        // A key limited to creating servers acts with that permission alone: the node's name and state, no more than
        // the create dialog needs, and the ports without the administrators' notes.
        var listed = await ListAsync<NodeSummary>(limited, "/api/v1/nodes");
        var slim = listed.Single(n => n.Id == node);
        Assert.Equal(("scoped-key-slim", NodeStatus.Active, true), (slim.Name, slim.Status, slim.Connected));
        Assert.All(listed, n => Assert.Equal(PanelKeyTrust.Unknown, n.PanelKeyTrust));
        Assert.All(listed, n => Assert.Equal((null, null, null, null, null, null, null, null, null),
            (n.Description, n.PublicHost, n.LastSeenAt, n.Inventory, n.Metrics, n.EnrollmentTokenName, n.CredentialFingerprint,
                n.ConnectedWithCertificate, n.AgentCanUseCertificate)));
        var ports = await ListAsync<AllocationView>(limited, $"/api/v1/nodes/{node}/allocations");
        Assert.Equal(2, ports.Count);
        Assert.All(ports, a => Assert.Null(a.Notes));
        using (var details = await limited.GetAsync(new Uri($"/api/v1/nodes/{node}", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, details.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task NodeUsageIsPushedOnlyToWhoSeesTheNode()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "live-slim");
        var node = agent.Config!.NodeId;
        var creatorRole = await CreateRoleAsync(scenario, "live-creator", PermissionScope.Node, Permissions.Server.Create);
        var viewerRole = await CreateRoleAsync(scenario, "live-viewer", PermissionScope.Node, Permissions.Node.View);
        var creator = await scenario.CreateUserAsync("live-creator-user");
        var viewer = await scenario.CreateUserAsync("live-viewer-user");
        await GrantAsync(scenario.Admin, creator.Id, "nodes", node, creatorRole.Id);
        await GrantAsync(scenario.Admin, viewer.Id, "nodes", node, viewerRole.Id);
        using var creatorClient = await scenario.SignInAsync("live-creator-user");
        using var viewerClient = await scenario.SignInAsync("live-viewer-user");
        using var everywhereClient = await scenario.SignedInWithAsync("live-global-creator", Permissions.Server.Create);
        await using var creatorHub = await BrowserHub.ConnectAsync(factory, creatorClient);
        await using var everywhereHub = await BrowserHub.ConnectAsync(factory, everywhereClient);
        await using var viewerHub = await BrowserHub.ConnectAsync(factory, viewerClient);
        await using var adminHub = await BrowserHub.ConnectAsync(factory, scenario.Admin);
        // The creators watch first: usage the others get after this was sent while they watched too.
        Assert.True(await creatorHub.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        Assert.True(await everywhereHub.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        Assert.True(await viewerHub.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        Assert.True(await adminHub.Connection.InvokeAsync<bool>("WatchNodes", Ct));

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(viewerHub.NodeMetrics.Any(m => m.NodeId == node)
            && adminHub.NodeMetrics.Any(m => m.NodeId == node)), "usage pushed to who sees the node");
        using (var renamed = await scenario.Admin.PutAsync($"/api/v1/nodes/{node}", new UpdateNodeRequest("scoped-live-renamed", null, null)))
        {
            await AccessScenario.EnsureSuccessAsync(renamed);
        }

        // A change is told to everyone who lists the node, after the usage they would have got (one connection keeps its order).
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(new[] { creatorHub, everywhereHub, viewerHub, adminHub }
            .All(h => h.NodesChanged.Contains(node))), "change pushed to who lists the node");
        Assert.Empty(creatorHub.NodeMetrics);
        Assert.Empty(everywhereHub.NodeMetrics);
    }

    [DatabaseFact]
    public async Task SeeingOneNodeAndCreatingServersOnAnotherShowsEachAsItsRoleSays()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var seen = await ConnectedAgentAsync(scenario.Admin, "mixed-seen");
        await using var created = await ConnectedAgentAsync(scenario.Admin, "mixed-created");
        var seenNode = seen.Config!.NodeId;
        var createdNode = created.Config!.NodeId;
        var viewerRole = await CreateRoleAsync(scenario, "mixed-viewer", PermissionScope.Node, Permissions.Node.View);
        var creatorRole = await CreateRoleAsync(scenario, "mixed-creator", PermissionScope.Node, Permissions.Server.Create);
        var user = await scenario.CreateUserAsync("mixed-user");
        await GrantAsync(scenario.Admin, user.Id, "nodes", seenNode, viewerRole.Id);
        await GrantAsync(scenario.Admin, user.Id, "nodes", createdNode, creatorRole.Id);
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(scenario.Admin, seenNode)).Node.Metrics is not null
            && (await NodeApi.GetAsync(scenario.Admin, createdNode)).Node.Metrics is not null, "usage reported");
        using var client = await scenario.SignInAsync("mixed-user");

        var listed = await ListAsync<NodeSummary>(client, "/api/v1/nodes");
        Assert.Equal([createdNode, seenNode], listed.Select(n => n.Id)); // by name: "scoped-mixed-created" first
        var full = listed.Single(n => n.Id == seenNode);
        Assert.NotNull(full.Inventory);
        Assert.NotNull(full.Metrics);
        Assert.NotNull(full.CredentialFingerprint);
        var slim = listed.Single(n => n.Id == createdNode);
        Assert.Equal((null, null, null, PanelKeyTrust.Unknown), (slim.Inventory, slim.Metrics, slim.CredentialFingerprint, slim.PanelKeyTrust));

        await using var browser = await BrowserHub.ConnectAsync(factory, client);
        Assert.True(await browser.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        // Usage of the node they see arrives; the other node reports as often, and its change is told after it.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(browser.NodeMetrics.Count(m => m.NodeId == seenNode) >= 2),
            "usage of the node they see");
        using (var renamed = await scenario.Admin.PutAsync($"/api/v1/nodes/{createdNode}", new UpdateNodeRequest("scoped-mixed-renamed", null, null)))
        {
            await AccessScenario.EnsureSuccessAsync(renamed);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(browser.NodesChanged.Contains(createdNode)), "change of the node they create on");
        Assert.All(browser.NodeMetrics, m => Assert.Equal(seenNode, m.NodeId));
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
    public async Task ChangingANodeRoleClosesTheLiveChannelAndTheReconnectionJoinsTheGroupsTheUserNowHas()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "regroup");
        var node = agent.Config!.NodeId;
        var role = await CreateRoleAsync(scenario, "regroup-host", PermissionScope.Node, Permissions.Node.View, Permissions.Server.Create);
        var host = await scenario.CreateUserAsync("regroup-user");
        await GrantAsync(scenario.Admin, host.Id, "nodes", node, role.Id);
        using var client = await scenario.SignInAsync("regroup-user");

        // With node.view on the node, the usage arrives.
        await using var seeing = await BrowserHub.ConnectAsync(factory, client);
        Assert.True(await seeing.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(seeing.NodeMetrics.Any(m => m.NodeId == node)), "usage of the node they see");

        // The role loses node.view: their connection ends, and the browser's reconnection watches again with what is
        // left, creating servers: the node's notices, not its usage.
        var closed = ClosedAsync(seeing.Connection);
        await UpdateRoleAsync(scenario, role, Permissions.Server.Create);
        await closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var creating = await BrowserHub.ConnectAsync(factory, client);
        Assert.True(await creating.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        // An administrator sees the usage whatever the role says (their connection ended with the role's change too):
        // once it has been pushed to them twice since the user watched, it would have reached the user if they saw it.
        await using var adminHub = await BrowserHub.ConnectAsync(factory, scenario.Admin);
        Assert.True(await adminHub.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(adminHub.NodeMetrics.Count(m => m.NodeId == node) >= 2),
            "usage pushed after the reconnection");
        using (var renamed = await scenario.Admin.PutAsync($"/api/v1/nodes/{node}", new UpdateNodeRequest("scoped-regroup-renamed", null, null)))
        {
            await AccessScenario.EnsureSuccessAsync(renamed);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(creating.NodesChanged.Contains(node)), "change of the node they create on");
        Assert.Empty(creating.NodeMetrics);

        // Given node.view again, the next reconnection gets the usage back.
        closed = ClosedAsync(creating.Connection);
        await UpdateRoleAsync(scenario, role, Permissions.Node.View, Permissions.Server.Create);
        await closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var seeingAgain = await BrowserHub.ConnectAsync(factory, client);
        Assert.True(await seeingAgain.Connection.InvokeAsync<bool>("WatchNodes", Ct));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(seeingAgain.NodeMetrics.Any(m => m.NodeId == node)), "usage of the node they see again");

        // Taking the role away leaves nothing to watch.
        closed = ClosedAsync(seeingAgain.Connection);
        using (var revoked = await scenario.Admin.DeleteAsync($"/api/v1/users/{host.Id}/nodes/{node}/roles/{role.Id}"))
        {
            await AccessScenario.EnsureSuccessAsync(revoked);
        }

        await closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await using var none = await BrowserHub.ConnectAsync(factory, client);
        Assert.False(await none.Connection.InvokeAsync<bool>("WatchNodes", Ct));
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

    [DatabaseFact]
    public async Task UserManagersCannotTakeOverUsersWhoseNodeOrServerRolesTheyLack()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "takeover");
        var node = agent.Config!.NodeId;
        var server = (await ServerApi.CreateAsync(scenario.Admin, node, "Takeover")).Server.Id;
        var keysRole = await CreateRoleAsync(scenario, "node-keys", PermissionScope.Node, Permissions.Node.CredentialsManage);
        var viewRole = await CreateRoleAsync(scenario, "srv-view-only", PermissionScope.Server, Permissions.Server.View);
        var filesRole = await CreateRoleAsync(scenario, "srv-files", PermissionScope.Server, Permissions.Server.View, Permissions.Server.FilesEdit);
        var viewer = await scenario.CreateRoleAsync("takeover-viewer", Permissions.User.View);
        var nodeUser = await scenario.CreateUserAsync("takeover-node", viewer);
        await GrantAsync(scenario.Admin, nodeUser.Id, "nodes", node, keysRole.Id);
        await GrantAsync(scenario.Admin, nodeUser.Id, "servers", server, viewRole.Id);
        var serverUser = await scenario.CreateUserAsync("takeover-server");
        await GrantAsync(scenario.Admin, serverUser.Id, "servers", server, filesRole.Id);
        using var manager = await scenario.SignedInWithAsync("takeover-manager", Permissions.User.Manage, Permissions.User.View, Permissions.Server.View);

        // Signing in as them would give the manager what they hold on that node or server.
        foreach (var target in new[] { nodeUser.Id, serverUser.Id })
        {
            await AssertCodeAsync(manager.PostAsync($"/api/v1/users/{target}/password", new ResetPasswordRequest("a new password for tests")),
                HttpStatusCode.Forbidden, "access.escalation");
            await AssertCodeAsync(manager.PostAsync($"/api/v1/users/{target}/two-factor/reset"), HttpStatusCode.Forbidden, "access.escalation");
            await AssertCodeAsync(manager.DeleteAsync($"/api/v1/users/{target}/api-keys"), HttpStatusCode.Forbidden, "access.escalation");
        }

        // Nor can they strip such a user of roles they hold themselves, globally or on a server.
        await AssertCodeAsync(manager.PutAsync($"/api/v1/users/{nodeUser.Id}/roles", new SetUserRolesRequest([])), HttpStatusCode.Forbidden,
            "access.escalation");
        await AssertCodeAsync(manager.DeleteAsync($"/api/v1/users/{nodeUser.Id}/servers/{server}/roles/{viewRole.Id}"), HttpStatusCode.Forbidden,
            "access.escalation");
        await AssertCodeAsync(manager.DeleteAsync($"/api/v1/users/{serverUser.Id}"), HttpStatusCode.Forbidden, "access.escalation");

        // Whoever holds those permissions there may.
        using var host = await scenario.SignedInWithAsync("takeover-host", Permissions.User.Manage, Permissions.User.View, Permissions.Server.View,
            Permissions.Node.CredentialsManage, Permissions.Server.FilesEdit);
        foreach (var target in new[] { nodeUser.Id, serverUser.Id })
        {
            using var reset = await host.PostAsync($"/api/v1/users/{target}/password", new ResetPasswordRequest("a new password for tests"));
            await AccessScenario.EnsureSuccessAsync(reset);
        }
    }

    [DatabaseFact]
    public async Task OnlyRolesOnTheNodeOrEverywhereChangeAServersLimits()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "limits");
        var server = (await ServerApi.CreateAsync(scenario.Admin, agent.Config!.NodeId, "Limited")).Server.Id;

        // The limits share the node's CPU and memory among its servers: no role on one server can hold the right.
        await AssertCodeAsync(scenario.Admin.PostAsync("/api/v1/roles",
                new CreateRoleRequest("srv-limits", null, PermissionScope.Server, [Permissions.Server.View, Permissions.Server.LimitsEdit])),
            HttpStatusCode.BadRequest, "role.permission_not_grantable");
        var serverRole = await CreateRoleAsync(scenario, "srv-tenant", PermissionScope.Server,
            Permissions.Server.View, Permissions.Server.Start, Permissions.Server.Stop, Permissions.Server.Install);
        var tenant = await scenario.CreateUserAsync("limits-tenant");
        await GrantAsync(scenario.Admin, tenant.Id, "servers", server, serverRole.Id);
        using (var client = await scenario.SignInAsync("limits-tenant"))
        {
            await AssertStatusAsync(client.PutAsync($"/api/v1/servers/{server}/limits", new ServerLimitsRequest(10, null)), HttpStatusCode.Forbidden);
        }

        var nodeRole = await CreateRoleAsync(scenario, "node-limits", PermissionScope.Node, Permissions.Server.View, Permissions.Server.LimitsEdit);
        var host = await scenario.CreateUserAsync("limits-host");
        await GrantAsync(scenario.Admin, host.Id, "nodes", agent.Config.NodeId, nodeRole.Id);
        using (var client = await scenario.SignInAsync("limits-host"))
        using (var changed = await client.PutAsync($"/api/v1/servers/{server}/limits", new ServerLimitsRequest(10, null)))
        {
            await AccessScenario.EnsureSuccessAsync(changed);
        }

        Assert.Equal(10, (await ServerApi.GetAsync(scenario.Admin, server))!.Limits.CpuPercent);
    }

    [DatabaseFact]
    public async Task ANodesPortsAreAllListedButNotTheServersTheCallerCannotSee()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(scenario.Admin, "ports");
        var node = agent.Config!.NodeId;
        var template = await GameServerTests.TemplateAsync(scenario.Admin, "css");
        using (var allocated = await scenario.Admin.PostAsync($"/api/v1/nodes/{node}/allocations",
                   new CreateAllocationsRequest("0.0.0.0", "27200-27202", "Forwarded on the rack 4 router")))
        {
            await AccessScenario.EnsureSuccessAsync(allocated);
        }

        var hidden = await CreatedAsync(scenario.Admin, new CreateServerRequest(node, template.Template.Id, "Hidden", null));
        var shown = await CreatedAsync(scenario.Admin, new CreateServerRequest(node, template.Template.Id, "Shown", null));
        var creator = await CreateRoleAsync(scenario, "node-creator", PermissionScope.Node, Permissions.Server.Create);
        var viewer = await CreateRoleAsync(scenario, "one-server-viewer", PermissionScope.Server, Permissions.Server.View);
        var user = await scenario.CreateUserAsync("ports-creator");
        await GrantAsync(scenario.Admin, user.Id, "nodes", node, creator.Id);
        await GrantAsync(scenario.Admin, user.Id, "servers", shown.Server.Id, viewer.Id);
        using var client = await scenario.SignInAsync("ports-creator");

        // Every port, with the holder of the one they see; the hidden server's port is taken, but its id and name are
        // not theirs to know.
        var listed = await ListAsync<AllocationView>(client, $"/api/v1/nodes/{node}/allocations");
        var shownPort = shown.Ports.Single(p => p.Name == "game").Port!.Value;
        var hiddenPort = hidden.Ports.Single(p => p.Name == "game").Port!.Value;
        Assert.Equal(Enumerable.Range(27200, 3), listed.Select(a => a.Port));
        Assert.Equal((shown.Server.Id, "Shown", "game", true),
            listed.Where(a => a.Port == shownPort).Select(a => (a.ServerId, a.ServerName, a.PortName, a.InUse)).Single());
        Assert.Equal((null, null, null, true),
            listed.Where(a => a.Port == hiddenPort).Select(a => (a.ServerId, a.ServerName, a.PortName, a.InUse)).Single());
        Assert.Equal((null, false), listed.Where(a => a.Port != shownPort && a.Port != hiddenPort).Select(a => (a.ServerId, a.InUse)).Single());
        Assert.All(listed, a => Assert.Null(a.Notes)); // the administrators' notes are the node's details

        // Whoever sees every server sees every holder; whoever sees the node, its notes.
        var all = await ListAsync<AllocationView>(scenario.Admin, $"/api/v1/nodes/{node}/allocations");
        Assert.Contains(all, a => a.ServerId == hidden.Server.Id && a.ServerName == "Hidden" && a.InUse);
        Assert.All(all, a => Assert.Equal("Forwarded on the rack 4 router", a.Notes));
        var nodeViewer = await CreateRoleAsync(scenario, "ports-node-viewer", PermissionScope.Node, Permissions.Node.View);
        var watcher = await scenario.CreateUserAsync("ports-node-watcher");
        await GrantAsync(scenario.Admin, watcher.Id, "nodes", node, nodeViewer.Id);
        using var watcherClient = await scenario.SignInAsync("ports-node-watcher");
        Assert.All(await ListAsync<AllocationView>(watcherClient, $"/api/v1/nodes/{node}/allocations"),
            a => Assert.Equal("Forwarded on the rack 4 router", a.Notes));
    }

    private static async Task<ServerDetails> CreatedAsync(PanelClient client, CreateServerRequest request)
    {
        using var response = await client.PostAsync("/api/v1/servers", request);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
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

    private static async Task UpdateRoleAsync(AccessScenario scenario, RoleSummary role, params string[] permissions)
    {
        using var response = await scenario.Admin.PutAsync($"/api/v1/roles/{role.Id}", new UpdateRoleRequest(role.Name, null, permissions));
        await AccessScenario.EnsureSuccessAsync(response);
    }

    /// <summary>Creates a key for the user <paramref name="client"/> is signed in as; null permissions: all of theirs.</summary>
    private static async Task<CreatedApiKey> CreateKeyAsync(PanelClient client, string name, List<string>? permissions)
    {
        using var response = await client.PostAsync("/api/v1/account/api-keys", new CreateApiKeyRequest(name, permissions, null));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKey>(AccessScenario.Json, Ct))!;
    }

    /// <summary>A script's client: no cookies, only the key.</summary>
    private HttpClient Bearer(string key)
    {
        var http = factory.CreateDefaultClient(new Uri("https://localhost"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    private static Task ClosedAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    private static Task<IReadOnlyList<T>> ListAsync<T>(PanelClient client, string path) => ListAsync<T>(client.Http, path);

    private static async Task<IReadOnlyList<T>> ListAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), Ct);
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
