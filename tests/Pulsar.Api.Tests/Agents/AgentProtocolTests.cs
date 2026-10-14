using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
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

/// <summary>Enrollment, authentication, the hub and revocation, end to end against the real panel (docs/architecture/03).</summary>
public sealed class AgentProtocolTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task EnrolledAgentConnectsAndAcceptsASignedPing()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-ping");
        await using var agent = new TestAgent(factory);

        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var token = await agent.AuthenticateAsync();
        var hello = await agent.ConnectAsync(token.AccessToken, AgentCapabilities.Ping);

        Assert.Equal(created.Node.Id, agent.NodeId);
        Assert.Contains(hello.PanelKeys, k => k.KeyId == created.Enrollment.PanelKeyFingerprint);

        var details = await GetNodeAsync(admin, agent.NodeId);
        Assert.True(details.Node.Connected);
        Assert.Equal(NodeStatus.Active, details.Node.Status);
        Assert.Equal("TEST-NODE", details.Node.Inventory?.Hostname);
        var credential = Assert.Single(details.Credentials);
        Assert.Equal(ProtocolCrypto.Fingerprint(agent.Key.ExportSubjectPublicKeyInfo()), credential.Fingerprint);

        using var ping = await admin.PostAsync($"/api/v1/nodes/{agent.NodeId}/ping");
        await AccessScenario.EnsureSuccessAsync(ping);
        var result = await ping.Content.ReadFromJsonAsync<PingResult>(AccessScenario.Json, Ct);
        Assert.Equal(AckStatus.Accepted, result!.Status);

        var command = Assert.Single(agent.ReceivedCommands);
        Assert.IsType<PingCommand>(command.Command);
        Assert.Equal(agent.NodeId, command.NodeId);
        Assert.Equal(token.SessionId, command.SessionId);
        Assert.Equal(1, command.Sequence);
        Assert.True(command.ExpiresAt > command.IssuedAt);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var nodeId = new NodeId(agent.NodeId);
        Assert.True(await db.AuditLog.AnyAsync(e => e.Action == AuditActions.Node.Enrolled && e.NodeId == nodeId, Ct));
    }

    [DatabaseFact]
    public async Task EnrollmentTokensWorkOnce()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-once");
        await using var first = new TestAgent(factory);
        await using var second = new TestAgent(factory);

        await first.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        using var reused = await second.PostEnrollAsync(created.Enrollment.Token);

        await AssertAgentErrorAsync(reused, "agent.enrollment_rejected");
    }

    [DatabaseFact]
    public async Task EnrollmentRequiresProofOfTheSubmittedKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-proof");
        await using var agent = new TestAgent(factory);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        using var forged = await agent.PostEnrollAsync(created.Enrollment.Token, proofKey: otherKey);
        await AssertAgentErrorAsync(forged, "agent.enrollment_rejected");

        // The rejected attempt did not consume the token.
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
    }

    [DatabaseFact]
    public async Task ExpiredEnrollmentTokensAreRejected()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-expired");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            var nodeId = new NodeId(created.Node.Id);
            await db.NodeEnrollmentTokens.Where(t => t.NodeId == nodeId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        }

        await using var agent = new TestAgent(factory);
        using var response = await agent.PostEnrollAsync(created.Enrollment.Token);

        await AssertAgentErrorAsync(response, "agent.enrollment_rejected");
    }

    [DatabaseFact]
    public async Task ChallengesAreSingleUseAndBoundToTheNodeKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-challenge");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var challenge = await agent.ChallengeAsync();
        using (var wrongKey = await agent.PostTokenAsync(challenge, signer: impostor))
        {
            // Said in the protocol's words: an agent tells the panel's refusal from a 401 of whatever else answers there.
            await AssertAgentErrorAsync(wrongKey, "agent.authentication_rejected", HttpStatusCode.Unauthorized);
        }

        // Even with the right key now, that challenge is gone.
        using (var reused = await agent.PostTokenAsync(challenge))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        }

        using var fresh = await agent.PostTokenAsync(await agent.ChallengeAsync());
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [DatabaseFact]
    public async Task TheSessionComesWithAGrantSignedForTheAgentsNonce()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-grant");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var nonce = RandomNumberGenerator.GetBytes(32);

        ChallengeResponse challenge;
        using (var asked = await agent.PostChallengeAsync(nonce))
        {
            asked.EnsureSuccessStatusCode();
            challenge = (await asked.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!;
        }

        var before = DateTimeOffset.UtcNow;
        using var response = await agent.PostTokenAsync(challenge);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(ProtocolJson.Options, Ct))!;
        var after = DateTimeOffset.UtcNow;

        // Signed by the key the node pinned, for this node, key and session, and for the nonce the agent chose (not the panel's).
        var grant = Assert.IsType<SignedMessage>(token.Grant);
        var pinned = Assert.Single(agent.PinnedKeys, k => k.KeyId == grant.KeyId);
        Assert.True(ProtocolCrypto.VerifyMessage(grant, AgentProtocol.Purposes.SessionGrant, pinned.PublicKey));
        var session = ProtocolJson.Deserialize<SessionGrant>(grant.Payload);
        Assert.Equal(agent.NodeId, session.NodeId);
        Assert.Equal(agent.CredentialId, session.CredentialId);
        Assert.Equal(token.SessionId, session.SessionId);
        Assert.Equal(token.ExpiresAt, session.ExpiresAt);
        Assert.Equal(nonce, session.Nonce);
        Assert.InRange(session.IssuedAt!.Value, before, after); // the panel's clock, which agents measure from it

        // An agent that sends no nonce (older) gets none: it would have nothing to check it against.
        Assert.Null((await agent.AuthenticateAsync()).Grant);
    }

    [DatabaseFact]
    public async Task TheNonceThePanelSignsBackIsAtMost64Bytes()
    {
        // The panel signs it back (a grant, an agents' address): a request cannot make it sign more than that.
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-long-nonce");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);

        using (var tooLong = await agent.PostChallengeAsync(new byte[AgentAccess.MaxChallengeNonceLength + 1]))
        {
            await AssertAgentErrorAsync(tooLong, "agent.authentication_rejected", HttpStatusCode.Unauthorized);
        }

        using (var longest = await agent.PostChallengeAsync(new byte[AgentAccess.MaxChallengeNonceLength]))
        {
            Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var nodeId = new NodeId(agent.NodeId);
        var refusals = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog
            .Where(e => e.Action == AuditActions.Node.AuthenticationFailed && e.NodeId == nodeId).ToListAsync(Ct);
        Assert.Single(refusals, e => e.Metadata.Contains("\"challenge_nonce_too_long\"", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task RevokingTheCredentialDisconnectsTheAgentImmediately()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-revoke");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, AgentCapabilities.Ping);

        using var revoke = await admin.PostAsync($"/api/v1/nodes/{agent.NodeId}/credentials/{agent.CredentialId}/revoke",
            new RevokeCredentialRequest("Key leaked"));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        await agent.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var challenge = await agent.PostChallengeAsync();
        await AssertAgentErrorAsync(challenge, "agent.authentication_rejected", HttpStatusCode.Unauthorized);
        Assert.False((await GetNodeAsync(admin, agent.NodeId)).Node.Connected);
    }

    [DatabaseFact]
    public async Task DisablingTheNodeDisconnectsItAndEnablingLetsItBack()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-disable");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, AgentCapabilities.Ping);

        using (var disable = await admin.PostAsync($"/api/v1/nodes/{agent.NodeId}/disable"))
        {
            await AccessScenario.EnsureSuccessAsync(disable);
        }

        await agent.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using (var refused = await agent.PostChallengeAsync())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using (var enable = await admin.PostAsync($"/api/v1/nodes/{agent.NodeId}/enable"))
        {
            await AccessScenario.EnsureSuccessAsync(enable);
        }

        Assert.Equal(HttpStatusCode.OK, (await agent.PostTokenAsync(await agent.ChallengeAsync())).StatusCode);
    }

    [DatabaseFact]
    public async Task ANodeDisabledWhileItSignsInGetsNoSession()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-disabled-at-sign-in");
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        await using var agent = new TestAgent(app);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var nodeId = new NodeId(agent.NodeId);
        var challenge = await agent.ChallengeAsync();

        // Disabled once the sign-in recorded its use of the key (and let the node's row go), before it has a session to end.
        hook.AfterCommit(c => c.ChangeTracker.Entries<NodeCredential>().Any(e => e.Entity.NodeId == nodeId), async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<NodeManagement>().DisableAsync(nodeId, CancellationToken.None)).IsSuccess);
        });
        using var signedIn = await agent.PostTokenAsync(challenge);

        Assert.Equal(HttpStatusCode.Unauthorized, signedIn.StatusCode);
    }

    [DatabaseFact]
    public async Task ASignInRacingARevocationOfItsKeyIsRefusedWithoutADeadlock()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-revoked-at-sign-in");
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var nodeId = new NodeId(agent.NodeId);
        var challenge = await agent.ChallengeAsync();

        // The revocation holds the node's row and has read the key when the sign-in records its use of the key and marks
        // the node seen; the revocation then writes the key.
        Task<HttpResponseMessage>? signingIn = null;
        hook.AfterQuery("FROM node_credentials", async () =>
        {
            signingIn = agent.PostTokenAsync(challenge);
            await WaitForALockWaitAsync();
        });
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<NodeManagement>()
                .RevokeCredentialAsync(nodeId, agent.CredentialId, "Key leaked", Ct)).IsSuccess);
        }

        using var signedIn = await signingIn!;
        Assert.Equal(HttpStatusCode.Unauthorized, signedIn.StatusCode);
    }

    [DatabaseFact]
    public async Task AConnectionWhoseSessionEndedIsNotHeard()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-session-ended");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, AgentCapabilities.Ping);
        var nodeId = new NodeId(agent.NodeId);
        var connections = factory.Services.GetRequiredService<IAgentConnections>();
        await agent.Connection!.InvokeAsync(nameof(IAgentHub.NodeMetrics), new NodeMetrics(DateTimeOffset.UtcNow, 5, 1, 2, TimeSpan.FromHours(1)), Ct);
        Assert.Equal(5, connections.Find(nodeId)!.LastMetrics!.CpuPercent);

        // As a rotation leaves the old key's connection until the new one says Hello.
        factory.Services.GetRequiredService<AgentSessionStore>().EndAll(nodeId);
        await agent.Connection!.InvokeAsync(nameof(IAgentHub.NodeMetrics), new NodeMetrics(DateTimeOffset.UtcNow, 50, 1, 2, TimeSpan.FromHours(1)), Ct);

        Assert.Equal(5, connections.Find(nodeId)?.LastMetrics?.CpuPercent ?? 5);
    }

    [DatabaseFact]
    public async Task UsageJsonCannotWriteIsNotPassedOn()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-infinite-cpu");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken);

        // 1e999 reads as infinity, which no JSON answer can carry: the node's page would fail for everyone.
        await agent.Connection!.InvokeAsync(nameof(IAgentHub.NodeMetrics),
            new RawJson($$"""{"at":"{{DateTimeOffset.UtcNow:O}}","cpuPercent":1e999,"memoryUsedBytes":1,"memoryTotalBytes":2,"uptime":"01:00:00"}"""), Ct);

        var metrics = (await GetNodeAsync(admin, agent.NodeId)).Node.Metrics;
        Assert.Equal(0, metrics!.CpuPercent);
    }

    [DatabaseFact]
    public async Task DrivesAnAgentReportsAreBoundedBeforeBrowsersSeeThem()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-many-drives");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken);
        var disks = Enumerable.Range(0, 40).Select(i => new DiskUsage($"D:\\{i}\n" + new string('x', 300), 100, 50, ["servers"])).ToList();

        await agent.Connection!.InvokeAsync(nameof(IAgentHub.NodeMetrics), new NodeMetrics(DateTimeOffset.UtcNow, 5, 1, 2, TimeSpan.FromHours(1), disks), Ct);

        var metrics = (await GetNodeAsync(admin, agent.NodeId)).Node.Metrics;
        Assert.Equal(AgentInput.MaxDisks, metrics!.Disks!.Count);
        Assert.All(metrics.Disks, d => Assert.True(d.Root.Length <= AgentInput.MaxDiskRootLength && !d.Root.Any(char.IsControl), d.Root));
    }

    [DatabaseFact]
    public async Task RefusedSignInsOfANodeAreAuditedWithoutFloodingTheLog()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-refused");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var nodeId = new NodeId(agent.NodeId);
        var challenge = await agent.ChallengeAsync();
        using (var disable = await admin.PostAsync($"/api/v1/nodes/{agent.NodeId}/disable"))
        {
            await AccessScenario.EnsureSuccessAsync(disable);
        }

        // The agent of a disabled node keeps trying (once a minute at most): one entry says so for a while.
        using (var token = await agent.PostTokenAsync(challenge))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);
        }

        for (var i = 0; i < 3; i++)
        {
            using var refused = await agent.PostChallengeAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // A node that does not exist is anyone's guess: logged, not audited.
        using (var unknown = await agent.Http.PostAsJsonAsync(AgentProtocol.ChallengePath, new ChallengeRequest(Guid.NewGuid(), Guid.NewGuid(), null),
            ProtocolJson.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var entries = await db.AuditLog.Where(e => e.Action == AuditActions.Node.AuthenticationFailed && e.NodeId == nodeId).ToListAsync(Ct);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(AuditOutcome.Failure, e.Outcome));
        Assert.Single(entries, e => e.Metadata.Contains("\"credential_or_node_inactive\"", StringComparison.Ordinal));
        Assert.Single(entries, e => e.Metadata.Contains("\"inactive_after_challenge\"", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task ChangingDisablingOrDeletingANodeIsNotLostToItsAgentsWrites()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var disabled = new NodeId((await CreateNodeAsync(admin, "node-disable-while-seen")).Node.Id);
        var deleted = new NodeId((await CreateNodeAsync(admin, "node-delete-while-seen")).Node.Id);
        var renamed = new NodeId((await CreateNodeAsync(admin, "node-rename-while-seen")).Node.Id);
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        await using var scope = app.Services.CreateAsyncScope();
        var nodes = scope.ServiceProvider.GetRequiredService<NodeManagement>();

        // The agent's Hello or metrics write the node's row between the panel's read and its save.
        Func<Task> seen(NodeId id) => async () =>
        {
            await using var other = factory.Services.CreateAsyncScope();
            await other.ServiceProvider.GetRequiredService<PulsarDbContext>().Nodes.Where(n => n.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastSeenAt, DateTimeOffset.UtcNow), CancellationToken.None);
        };
        hook.BeforeSave(c => c.ChangeTracker.Entries<Node>().Any(e => e.Entity.Id == disabled && e.State == EntityState.Modified), seen(disabled));
        Assert.True((await nodes.DisableAsync(disabled, Ct)).IsSuccess);
        hook.BeforeSave(c => c.ChangeTracker.Entries<Node>().Any(e => e.Entity.Id == deleted && e.State == EntityState.Deleted), seen(deleted));
        Assert.True((await nodes.DeleteAsync(deleted, Ct)).IsSuccess);
        hook.BeforeSave(c => c.ChangeTracker.Entries<Node>().Any(e => e.Entity.Id == renamed && e.State == EntityState.Modified), seen(renamed));
        Assert.True((await nodes.UpdateAsync(renamed, "node-renamed-while-seen", "Renamed", null, Ct)).IsSuccess);

        Assert.Equal(NodeStatus.Disabled, (await GetNodeAsync(admin, disabled.Value)).Node.Status);
        using var gone = await admin.GetAsync($"/api/v1/nodes/{deleted}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        var node = (await GetNodeAsync(admin, renamed.Value)).Node;
        Assert.Equal(("node-renamed-while-seen", "Renamed"), (node.Name, node.Description));
    }

    [DatabaseFact]
    public async Task HubRefusesConnectionsWithoutAValidSession()
    {
        await using var agent = new TestAgent(factory);

        await Assert.ThrowsAnyAsync<Exception>(() => agent.ConnectAsync("not-a-session-token"));
    }

    [DatabaseFact]
    public async Task PingingADisconnectedNodeIsAConflict()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "node-offline");

        using var ping = await admin.PostAsync($"/api/v1/nodes/{created.Node.Id}/ping");

        await Auth.SetupTests.AssertProblemAsync(ping, HttpStatusCode.Conflict, "node.not_connected");
    }

    private static async Task<CreatedNode> CreateNodeAsync(PanelClient admin, string name)
    {
        using var response = await admin.PostAsync("/api/v1/nodes", new CreateNodeRequest(name, null, null));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreatedNode>(AccessScenario.Json, Ct))!;
    }

    private static async Task<NodeDetails> GetNodeAsync(PanelClient admin, Guid id) =>
        (await admin.Http.GetFromJsonAsync<NodeDetails>(new Uri($"/api/v1/nodes/{id}", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static async Task AssertAgentErrorAsync(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, Ct);
        Assert.Equal(code, error!.Code);
    }

    /// <summary>Until a connection to the test's database waits for a lock another one holds.</summary>
    private async Task WaitForALockWaitAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync(Ct) == 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Nothing waited for a lock.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }

    /// <summary>A hub argument sent as written, for what the agent's own serializer would never write.</summary>
    [JsonConverter(typeof(RawJsonConverter))]
    private sealed record RawJson(string Json);

    private sealed class RawJsonConverter : JsonConverter<RawJson>
    {
        public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.Json, skipInputValidation: true);
    }
}
