using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Panel;
using Pulsar.Api.Nodes;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// The agent's own code (Pulsar.Agent.Core) against the real panel: the Phase 2 "done" scenario minus the Windows-specific
/// parts — pairs, appears online with its inventory, survives connection loss, renews its session, and stays out once revoked.
/// </summary>
public sealed class RealAgentTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task EnrolledAgentComesOnlineWithInventoryAndMetricsAndAnswersPings()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-online");
        await using var agent = new InProcessAgent(factory);

        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();

        Assert.Equal(created.Node.Id, config.NodeId);
        Assert.Equal(new Uri("https://localhost/"), config.Panel);
        Assert.Contains(config.PanelKeys, k => k.KeyId == created.Enrollment.PanelKeyFingerprint);
        await InProcessAgent.EventuallyAsync(async () => (await GetNodeAsync(admin, config.NodeId)).Node.Metrics is not null, "metrics");

        var node = (await GetNodeAsync(admin, config.NodeId)).Node;
        Assert.True(node.Connected);
        Assert.Equal("TEST-NODE", node.Inventory!.Hostname);
        Assert.Equal(agent.ServersRoot, node.Inventory.ServersRoot);
        Assert.Equal(12.5, node.Metrics!.CpuPercent);
        Assert.Equal(AckStatus.Accepted, (await PingAsync(admin, config.NodeId)).Status);
    }

    [DatabaseFact]
    public async Task NodeMetricsGoOnAfterASampleFails()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-metrics-failure");
        await using var agent = new InProcessAgent(factory) { FailingMetricSamples = 1 };
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);

        await agent.StartAsync();

        await InProcessAgent.EventuallyAsync(async () => (await GetNodeAsync(admin, config.NodeId)).Node.Metrics is not null, "metrics after the failed sample");
        Assert.True(agent.FailingMetricSamples < 0);
    }

    [DatabaseFact]
    public async Task AgentReconnectsByItselfAfterTheConnectionIsLost()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-reconnect");
        await using var agent = new InProcessAgent(factory);
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await GetNodeAsync(admin, config.NodeId)).Node.Connected, "first connection");
        var firstSession = agent.CurrentSessionId;

        // What a network cut looks like from the panel: the connection is gone without the agent asking.
        factory.Services.GetRequiredService<IAgentConnections>().Disconnect(new NodeId(config.NodeId));

        await InProcessAgent.EventuallyAsync(async () => agent.CurrentSessionId is { } s && s != firstSession
            && (await GetNodeAsync(admin, config.NodeId)).Node.Connected, "reconnection with a new session");
        Assert.Equal(AckStatus.Accepted, (await PingAsync(admin, config.NodeId)).Status);
    }

    [DatabaseFact]
    public async Task AgentRenewsItsSessionBeforeItExpiresWithoutGoingOffline()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-renew");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var agent = new InProcessAgent(factory, clock);
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await GetNodeAsync(admin, config.NodeId)).Node.Connected, "first connection");
        var firstSession = agent.CurrentSessionId;

        // Only the agent's clock moves: it reaches the renewal point of a 15-minute session.
        clock.Advance(AgentSessionStore.Lifetime);

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentSessionId is { } s && s != firstSession), "renewed session");
        Assert.True((await GetNodeAsync(admin, config.NodeId)).Node.Connected);
        Assert.Equal(AckStatus.Accepted, (await PingAsync(admin, config.NodeId)).Status);
    }

    [DatabaseFact]
    public async Task RevokedAgentIsCutOffAndCannotComeBack()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-revoke");
        await using var agent = new InProcessAgent(factory);
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await GetNodeAsync(admin, config.NodeId)).Node.Connected, "connection");

        using (var revoke = await admin.PostAsync($"/api/v1/nodes/{config.NodeId}/credentials/{config.CredentialId}/revoke",
            new RevokeCredentialRequest("Test")))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentSessionId is null), "disconnection");
        // Give the agent time for several reconnect attempts: every one must be refused.
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Null(agent.CurrentSessionId);
        Assert.False((await GetNodeAsync(admin, config.NodeId)).Node.Connected);
    }

    [DatabaseFact]
    public async Task EnrollmentRefusesAPanelThatDoesNotHoldThePinnedKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-wrong-key");
        await using var agent = new InProcessAgent(factory);

        var error = await Assert.ThrowsAsync<AgentException>(() =>
            agent.EnrollAsync(created.Enrollment.Token, "sha256:" + new string('A', 43)));

        Assert.Contains("does not hold the key", error.Message, StringComparison.Ordinal);
        Assert.Empty(agent.Keys.Names);
        Assert.Null(agent.Configs.Load());
        // Nothing reached the panel: the token is still usable.
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
    }

    [DatabaseFact]
    public async Task EnrollmentRefusesAnAnswerBoundToAnotherOrigin()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-origin");
        await using var agent = new InProcessAgent(factory);

        // The agent was told one origin, but its requests are relayed to the panel at https://localhost, which signs for that.
        var error = await Assert.ThrowsAsync<AgentException>(() =>
            agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint, panel: "https://panel.example.com"));

        Assert.Contains("answered for https://localhost", error.Message, StringComparison.Ordinal);
        Assert.Empty(agent.Keys.Names);
        Assert.Null(agent.Configs.Load());
    }

    [DatabaseFact]
    public async Task ReEnrollingReplacesTheNodeKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-reenroll");
        await using var agent = new InProcessAgent(factory);
        var first = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);

        using var tokenResponse = await admin.PostAsync($"/api/v1/nodes/{first.NodeId}/enrollment-tokens");
        await AccessScenario.EnsureSuccessAsync(tokenResponse);
        var instructions = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentInstructions>(AccessScenario.Json, Ct))!;
        var second = await agent.EnrollAsync(instructions.Token, instructions.PanelKeyFingerprint);

        Assert.Equal(first.NodeId, second.NodeId);
        Assert.NotEqual(first.CredentialId, second.CredentialId);
        Assert.Equal([second.Key.Name], agent.Keys.Names);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await GetNodeAsync(admin, second.NodeId)).Node.Connected, "connection with the new key");
    }

    private static async Task<CreatedNode> CreateNodeAsync(PanelClient admin, string name)
    {
        using var response = await admin.PostAsync("/api/v1/nodes", new CreateNodeRequest(name, null, null));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreatedNode>(AccessScenario.Json, Ct))!;
    }

    private static async Task<NodeDetails> GetNodeAsync(PanelClient admin, Guid id) =>
        (await admin.Http.GetFromJsonAsync<NodeDetails>(new Uri($"/api/v1/nodes/{id}", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static async Task<PingResult> PingAsync(PanelClient admin, Guid id)
    {
        using var ping = await admin.PostAsync($"/api/v1/nodes/{id}/ping");
        await AccessScenario.EnsureSuccessAsync(ping);
        return (await ping.Content.ReadFromJsonAsync<PingResult>(AccessScenario.Json, Ct))!;
    }
}
