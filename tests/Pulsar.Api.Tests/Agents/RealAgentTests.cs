using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Panel;
using Pulsar.Api.Nodes;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

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
        // The panel says connected at Hello; the agent has its session once Hello is answered.
        await InProcessAgent.EventuallyAsync(async () => agent.CurrentSessionId is not null && (await GetNodeAsync(admin, config.NodeId)).Node.Connected,
            "first connection");
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
        // The panel says connected at Hello; the agent has its session once Hello is answered.
        await InProcessAgent.EventuallyAsync(async () => agent.CurrentSessionId is not null && (await GetNodeAsync(admin, config.NodeId)).Node.Connected,
            "first connection");
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

    [DatabaseFact]
    public async Task ASessionHolderCannotLockTheNodeOutByLyingAboutItsPinnedKeys()
    {
        // Whoever holds the agent's session token (a TLS-terminating proxy, a copied token) says Hello with panel keys the
        // agent does not pin. The agent's next sign-ins name their own keys, so their grants still verify.
        using var admin = await factory.CreateAdminClientAsync();
        var created = await CreateNodeAsync(admin, "real-pins-lie");
        await using var agent = new InProcessAgent(factory);
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        string? stolen = null;
        agent.Proxy = async (request, send) =>
        {
            var response = await send();
            if (request.RequestUri!.AbsolutePath == AgentProtocol.TokenPath && response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
                stolen = JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                    ProtocolJson.Options)!.AccessToken;
            }

            return response;
        };
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => agent.CurrentSessionId is not null && (await GetNodeAsync(admin, config.NodeId)).Node.Connected,
            "first connection");
        var first = agent.CurrentSessionId;

        // An empty list is no list of pins: that Hello is refused, and the agent's connection stays.
        await Assert.ThrowsAnyAsync<Exception>(() => HelloAsync(stolen!, []));
        Assert.Equal(first, agent.CurrentSessionId);

        // A well-formed lie is taken (and audited), and cuts the agent's connection off: it signs in again, and stays.
        await using (await HelloAsync(stolen!, ["sha256:not-a-panel-key"]))
        {
            await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentSessionId != first), "the agent cut off");
        }

        await InProcessAgent.EventuallyAsync(async () => agent.CurrentSessionId is { } s && s != first && (await GetNodeAsync(admin, config.NodeId)).Node.Connected,
            "the agent connected again");
        Assert.Equal(AckStatus.Accepted, (await PingAsync(admin, config.NodeId)).Status);
        await using var scope = factory.Services.CreateAsyncScope();
        var nodeId = new NodeId(config.NodeId);
        Assert.True(await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog
            .AnyAsync(e => e.Action == AuditActions.Node.PanelKeysPinned && e.NodeId == nodeId, TestContext.Current.CancellationToken));
    }

    /// <summary>Says Hello on the agents' hub with a session token, as whoever copied it would.</summary>
    private async Task<HubConnection> HelloAsync(string accessToken, IReadOnlyList<string> pinned)
    {
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri("wss://localhost" + AgentProtocol.HubPath), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {accessToken}";
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(options => ProtocolJson.Configure(options.PayloadSerializerOptions))
            .Build();
        try
        {
            await connection.StartAsync(TestContext.Current.CancellationToken);
            await connection.InvokeAsync<HelloAccepted>(nameof(IAgentHub.Hello),
                new AgentHello("0.1.0-thief", AgentProtocol.CurrentVersion, [], TestAgent.Inventory, pinned), TestContext.Current.CancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
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
