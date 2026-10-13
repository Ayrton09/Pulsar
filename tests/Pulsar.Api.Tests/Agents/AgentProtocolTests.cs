using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
}
