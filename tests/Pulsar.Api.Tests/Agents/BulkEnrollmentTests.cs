using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Nodes;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Agents;

/// <summary>Reusable enrollment tokens (Phase 14, docs/architecture/03, "Bulk enrollment"), end to end against the real panel.</summary>
public sealed class BulkEnrollmentTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AReusableTokenEnrollsEachMachineAsANewNodeUntilItsUsesRunOut()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var token = await CreateTokenAsync(admin, "Fleet", maxUses: 2, requireApproval: false);
        Assert.StartsWith(ReusableEnrollmentToken.Prefix, token.Value, StringComparison.Ordinal);
        await using var first = new TestAgent(factory) { Hostname = "FLEET-01" };
        await using var second = new TestAgent(factory) { Hostname = "fleet-01" }; // the same name, another machine
        await using var third = new TestAgent(factory) { Hostname = "FLEET-03" };

        await first.EnrollAsync(token.Value, token.PanelKeyFingerprint);
        await second.EnrollAsync(token.Value, token.PanelKeyFingerprint);
        using var refused = await third.PostEnrollAsync(token.Value);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.NotEqual(first.NodeId, second.NodeId);
        Assert.False(first.AwaitingApproval);
        var one = await GetNodeAsync(admin, first.NodeId);
        var two = await GetNodeAsync(admin, second.NodeId);
        Assert.Equal(("FLEET-01", NodeStatus.Active), (one.Node.Name, one.Node.Status));
        Assert.Equal(("fleet-01-2", NodeStatus.Active), (two.Node.Name, two.Node.Status));

        // Active right away: the agent connects.
        var session = await first.AuthenticateAsync();
        await first.ConnectAsync(session.AccessToken, AgentCapabilities.Ping);
        Assert.True((await GetNodeAsync(admin, first.NodeId)).Node.Connected);

        var listed = Assert.Single(await ListTokensAsync(admin), t => t.Id == token.Token.Id);
        Assert.Equal((2, false), (listed.Uses, listed.Usable));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var nodeId = new NodeId(first.NodeId);
        Assert.True(await db.AuditLog.AnyAsync(e => e.Action == AuditActions.Node.Created && e.NodeId == nodeId, Ct));
        Assert.True(await db.AuditLog.AnyAsync(e => e.Action == AuditActions.EnrollmentToken.Created, Ct));
        Assert.Equal(token.Token.Id, (await db.Nodes.SingleAsync(n => n.Id == nodeId, Ct)).EnrollmentTokenId);
    }

    [DatabaseFact]
    public async Task NodesOfATokenThatRequiresApprovalWaitForItBeforeTheirAgentsConnect()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var token = await CreateTokenAsync(admin, "Waits", maxUses: 5, requireApproval: true);
        await using var first = new TestAgent(factory) { Hostname = "WAIT-01" };
        await using var second = new TestAgent(factory) { Hostname = "WAIT-02" };
        await first.EnrollAsync(token.Value, token.PanelKeyFingerprint);
        await second.EnrollAsync(token.Value, token.PanelKeyFingerprint);

        Assert.True(first.AwaitingApproval);
        Assert.Equal(NodeStatus.PendingApproval, (await GetNodeAsync(admin, first.NodeId)).Node.Status);
        using (var challenge = await first.PostChallengeAsync())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        }

        // Disabling and enabling it does not skip the approval.
        await AccessScenario.EnsureSuccessAsync(await admin.PostAsync($"/api/v1/nodes/{first.NodeId}/disable"));
        await AccessScenario.EnsureSuccessAsync(await admin.PostAsync($"/api/v1/nodes/{first.NodeId}/enable"));
        Assert.Equal(NodeStatus.PendingApproval, (await GetNodeAsync(admin, first.NodeId)).Node.Status);

        using var approved = await admin.PostAsync("/api/v1/nodes/approve", new ApproveNodesRequest([first.NodeId, second.NodeId]));
        await AccessScenario.EnsureSuccessAsync(approved);
        var nodes = (await approved.Content.ReadFromJsonAsync<List<NodeSummary>>(AccessScenario.Json, Ct))!;
        Assert.All(nodes, n => Assert.Equal(NodeStatus.Active, n.Status));
        var session = await first.AuthenticateAsync();
        await first.ConnectAsync(session.AccessToken, AgentCapabilities.Ping);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var nodeId = new NodeId(first.NodeId);
        Assert.True(await db.AuditLog.AnyAsync(e => e.Action == AuditActions.Node.Approved && e.NodeId == nodeId, Ct));
    }

    [DatabaseFact]
    public async Task ARevokedTokenEnrollsNobodyAndNoTokenTakesOverAnExistingNode()
    {
        using var admin = await factory.CreateAdminClientAsync();
        using (var existing = await admin.PostAsync("/api/v1/nodes", new CreateNodeRequest("KEEP-ME", "made by hand", null)))
        {
            await AccessScenario.EnsureSuccessAsync(existing);
        }

        var token = await CreateTokenAsync(admin, "Short lived", maxUses: 10, requireApproval: false);
        await using var agent = new TestAgent(factory) { Hostname = "keep-me" };
        await agent.EnrollAsync(token.Value, token.PanelKeyFingerprint);
        var created = await GetNodeAsync(admin, agent.NodeId);
        Assert.Equal("keep-me-2", created.Node.Name);

        await AccessScenario.EnsureSuccessAsync(await admin.PostAsync($"/api/v1/enrollment-tokens/{token.Token.Id}/revoke"));
        await using var late = new TestAgent(factory) { Hostname = "LATE" };
        using var refused = await late.PostEnrollAsync(token.Value);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(Assert.Single(await ListTokensAsync(admin), t => t.Id == token.Token.Id).Usable);
        Assert.Equal(NodeStatus.Active, (await GetNodeAsync(admin, agent.NodeId)).Node.Status); // its nodes stay
    }

    [DatabaseFact]
    public async Task TokensAreCheckedWhenCreated()
    {
        using var admin = await factory.CreateAdminClientAsync();

        using var tooMany = await admin.PostAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Big", 1001, 24, true));
        using var tooLong = await admin.PostAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Old", 10, 91 * 24, true));

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    private static async Task<CreatedEnrollmentToken> CreateTokenAsync(PanelClient admin, string name, int maxUses, bool requireApproval)
    {
        using var response = await admin.PostAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest(name, maxUses, 24, requireApproval));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreatedEnrollmentToken>(AccessScenario.Json, Ct))!;
    }

    private static async Task<IReadOnlyList<EnrollmentTokenView>> ListTokensAsync(PanelClient admin) =>
        (await admin.Http.GetFromJsonAsync<List<EnrollmentTokenView>>(new Uri("/api/v1/enrollment-tokens", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static async Task<NodeDetails> GetNodeAsync(PanelClient admin, Guid id) =>
        (await admin.Http.GetFromJsonAsync<NodeDetails>(new Uri($"/api/v1/nodes/{id}", UriKind.Relative), AccessScenario.Json, Ct))!;
}
