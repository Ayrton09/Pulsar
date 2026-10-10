using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pulsar.Api.Access;
using Pulsar.Api.Nodes;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
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

    [DatabaseFact]
    public async Task MachinesRacingForTheLastUsesEnrollNoMoreThanTheTokenAllows()
    {
        using var admin = await factory.CreateAdminClientAsync();
        const int uses = 4;
        var token = await CreateTokenAsync(admin, "Race", maxUses: uses, requireApproval: false);
        var agents = Enumerable.Range(1, uses + 3).Select(i => new TestAgent(factory) { Hostname = $"RACE-{i:00}" }).ToList();
        try
        {
            var responses = await Task.WhenAll(agents.Select(a => a.PostEnrollAsync(token.Value)));
            var statuses = responses.Select(r => r.StatusCode).ToList();
            foreach (var response in responses)
            {
                response.Dispose();
            }

            Assert.Equal(uses, statuses.Count(s => s == HttpStatusCode.OK));
            Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.BadRequest));
            Assert.Equal(uses, Assert.Single(await ListTokensAsync(admin), t => t.Id == token.Token.Id).Uses);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            Assert.Equal(uses, await db.Nodes.CountAsync(n => n.EnrollmentTokenId == token.Token.Id, Ct));
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }
        }
    }

    [DatabaseFact]
    public async Task AnExpiredTokenEnrollsNobodyAndTheAuditSaysWhichToken()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var token = await CreateTokenAsync(admin, "Expired", maxUses: 10, requireApproval: false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            await db.ReusableEnrollmentTokens.Where(t => t.Id == token.Token.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        }

        await using var agent = new TestAgent(factory) { Hostname = "TOO-LATE" };
        using var refused = await agent.PostEnrollAsync(token.Value);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var listed = Assert.Single(await ListTokensAsync(admin), t => t.Id == token.Token.Id);
        Assert.Equal((0, false), (listed.Uses, listed.Usable));
        await using var check = factory.Services.CreateAsyncScope();
        var audit = await check.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Action == AuditActions.Node.EnrollmentFailed).Select(e => e.Metadata).ToListAsync(Ct);
        Assert.Contains(audit, m => m.Contains("reusable_token_unusable", StringComparison.Ordinal)
            && m.Contains(token.Token.Id.ToString(), StringComparison.Ordinal) && m.Contains("Expired", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task ANameTakenWhileEnrollingGivesTheNodeAnotherOne()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var token = await CreateTokenAsync(admin, "Collide", maxUses: 5, requireApproval: false);
        await using var agent = new TestAgent(factory) { Hostname = "COLLIDE-01" };

        // The token's row held by another transaction: the enrollment picks its name, then waits to count its use.
        await using var holder = new NpgsqlConnection(factory.Database.ConnectionString);
        await holder.OpenAsync(Ct);
        await using var hold = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM reusable_enrollment_tokens WHERE id = @id FOR UPDATE", holder, hold))
        {
            lockRow.Parameters.AddWithValue("id", token.Token.Id);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var enrolling = agent.PostEnrollAsync(token.Value);
        await InProcessAgent.EventuallyAsync(async () =>
        {
            // From another connection: pg_stat_activity is read once per transaction, so the holder's would never change.
            await using var watcher = new NpgsqlConnection(factory.Database.ConnectionString);
            await watcher.OpenAsync(Ct);
            await using var waiting = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", watcher);
            return (long)(await waiting.ExecuteScalarAsync(Ct))! > 0;
        }, "enrollment waiting for the token");

        // Meanwhile an administrator creates a node of that name.
        using (var created = await admin.PostAsync("/api/v1/nodes", new CreateNodeRequest("collide-01", null, null)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
        }

        await hold.RollbackAsync(Ct);
        using var response = await enrolling;
        await AccessScenario.EnsureSuccessAsync(response);
        var result = ProtocolJson.Deserialize<EnrollmentResult>((await response.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!.Payload);
        var node = await GetNodeAsync(admin, result.NodeId);
        Assert.Equal($"COLLIDE-01-{result.NodeId.ToString("N")[^8..]}", node.Node.Name);
        Assert.Equal(1, Assert.Single(await ListTokensAsync(admin), t => t.Id == token.Token.Id).Uses);
    }

    [DatabaseFact]
    public async Task AKeyAlreadyRegisteredIsRefusedWithoutUsingTheToken()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var token = await CreateTokenAsync(admin, "Replayed", maxUses: 5, requireApproval: false);
        await using var agent = new TestAgent(factory) { Hostname = "REPLAY-01" };
        await agent.EnrollAsync(token.Value, token.PanelKeyFingerprint);

        // The same request again (replayed): another node with the same key would break the fingerprint's unique index.
        using var replayed = await agent.PostEnrollAsync(token.Value);

        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(1, Assert.Single(await ListTokensAsync(admin), t => t.Id == token.Token.Id).Uses);
    }

    [DatabaseFact]
    public async Task InvisibleAndOversizedCharactersOfTheMachineNameAreDropped()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var token = await CreateTokenAsync(admin, "Unicode", maxUses: 5, requireApproval: true);
        // Zero width space, right-to-left override, isolate and line separator: PROD-GAME-01 lookalikes.
        await using var hidden = new TestAgent(factory) { Hostname = $"PROD-{(char)0x200B}GAME{(char)0x202E}-01{(char)0x2066}{(char)0x2028}" };
        // A long name whose cut would fall inside a surrogate pair, for the node's name and for the inventory.
        await using var longName = new TestAgent(factory) { Hostname = new string('A', 59) + "\U0001F600" + new string('B', 193) + "\U0001F600" };

        await hidden.EnrollAsync(token.Value, token.PanelKeyFingerprint);
        await longName.EnrollAsync(token.Value, token.PanelKeyFingerprint);

        var node = (await GetNodeAsync(admin, hidden.NodeId)).Node;
        Assert.Equal(("PROD-GAME-01", "PROD-GAME-01"), (node.Name, node.Inventory!.Hostname));
        var cut = (await GetNodeAsync(admin, longName.NodeId)).Node;
        Assert.Equal(new string('A', 59), cut.Name);
        Assert.Equal(new string('A', 59) + "\U0001F600" + new string('B', 193), cut.Inventory!.Hostname);
    }

    [DatabaseFact]
    public async Task AWaitingNodeShowsItsTokenAndKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var token = await CreateTokenAsync(admin, "Shown", maxUses: 5, requireApproval: true);
        await using var agent = new TestAgent(factory) { Hostname = "SHOWN-01" };
        await agent.EnrollAsync(token.Value, token.PanelKeyFingerprint);

        var listed = Assert.Single((await admin.Http.GetFromJsonAsync<List<NodeSummary>>(new Uri("/api/v1/nodes", UriKind.Relative), AccessScenario.Json, Ct))!,
            n => n.Id == agent.NodeId);

        Assert.Equal("Shown", listed.EnrollmentTokenName);
        Assert.Equal(ProtocolCrypto.Fingerprint(agent.Key.ExportSubjectPublicKeyInfo()), listed.CredentialFingerprint);
    }

    [DatabaseFact]
    public async Task ApprovingManyAndReusableTokensNeedTheirPermissionsGlobally()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var node = await NodeApi.CreateAsync(scenario.Admin, "scoped-approver-node");
        using var roleResponse = await scenario.Admin.PostAsync("/api/v1/roles", new CreateRoleRequest("scoped-node-admin", null, PermissionScope.Node,
            [Permissions.Node.View, Permissions.Node.Manage, Permissions.Node.CredentialsManage]));
        await AccessScenario.EnsureSuccessAsync(roleResponse);
        var role = (await roleResponse.Content.ReadFromJsonAsync<RoleSummary>(AccessScenario.Json, Ct))!;
        var user = await scenario.CreateUserAsync("scoped-approver");
        using (var granted = await scenario.Admin.PutAsync($"/api/v1/users/{user.Id}/nodes/{node.Node.Id}/roles/{role.Id}", new { }))
        {
            await AccessScenario.EnsureSuccessAsync(granted);
        }

        using var client = await scenario.SignInAsync("scoped-approver");

        using var approve = await client.PostAsync("/api/v1/nodes/approve", new ApproveNodesRequest([node.Node.Id]));
        using var list = await client.GetAsync("/api/v1/enrollment-tokens");
        using var create = await client.PostAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Mine", 5, 24, true));
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [DatabaseFact]
    public async Task EnrollmentsOverTheAddressLimitAreToldWhenToComeBack()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Auth:EnrollPermitLimit", "2"));
        await using var agent = new TestAgent(limited) { Hostname = "BUSY-01" };
        for (var i = 0; i < 2; i++)
        {
            using var refused = await agent.PostEnrollAsync("pulsar_enroll_not-a-token");
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using var busy = await agent.PostEnrollAsync("pulsar_enroll_not-a-token");

        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.InRange(busy.Headers.RetryAfter?.Delta ?? TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
    }

    [DatabaseFact]
    public async Task TheFiftyFirstUsableTokenIsRefused()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var usable = (await ListTokensAsync(admin)).Count(t => t.Usable);
        List<Guid> created = [];
        try
        {
            for (var i = usable; i < EnrollmentTokens.MaxUsable; i++)
            {
                created.Add((await CreateTokenAsync(admin, $"Filler {i}", maxUses: 1, requireApproval: true)).Token.Id);
            }

            using var refused = await admin.PostAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("One more", 1, 24, true));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("\"enrollment_token.too_many\"", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var id in created)
            {
                using var revoked = await admin.PostAsync($"/api/v1/enrollment-tokens/{id}/revoke");
            }
        }
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
