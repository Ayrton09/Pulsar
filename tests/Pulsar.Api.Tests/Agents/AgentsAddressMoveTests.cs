using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// The real agent core and the agents' address (Phase 16): told it by a command, it signs in there with its node key as
/// client certificate and stays; a closed agents' port sends it back to the address it came from; a panel that requires
/// certificates sends it there with a signed refusal.
/// </summary>
public sealed class AgentsAddressMoveTests(AgentsEndpointApiFactory factory) : IClassFixture<AgentsEndpointApiFactory>
{
    private static readonly Uri s_main = new("https://localhost/");
    private static readonly Uri s_agents = new($"https://localhost:{AgentsEndpointApiFactory.AgentsPort}/");

    [DatabaseFact]
    public async Task AnAgentMovesToTheAgentsAddressAndKeepsTheOldOneAsFallback()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "move-command");
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);

        await agent.StartAsync();

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == s_agents), "the agent on the agents' address");
        Assert.Equal(s_agents, agent.LiveConfig!.Panel);
        Assert.Equal(s_main, agent.LiveConfig.FallbackPanel);
        Assert.Equal(s_agents, agent.Configs.Load()!.Panel); // saved: it survives a restart
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, config.NodeId)).Node.ConnectedWithCertificate == true, "recorded");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
    }

    [DatabaseFact]
    public async Task AnAgentEnrollsThroughTheAgentsAddressWithItsNewKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "move-enroll");

        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint, s_agents.ToString());

        Assert.Equal(s_agents, config.Panel);
        Assert.Null(config.FallbackPanel);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == s_agents && agent.CurrentSessionId is not null), "connected");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
        Assert.True((await NodeApi.GetAsync(admin, config.NodeId)).Node.ConnectedWithCertificate);
    }

    [DatabaseFact]
    public async Task AClosedAgentsPortSendsTheAgentBackToItsFallbackUntilItOpensAgain()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "move-fallback");
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == s_agents), "the agent on the agents' address");

        agent.AgentsEndpointDown = true;
        Disconnect(config.NodeId);

        // Back on the old address: told the agents' address again, it cannot sign in there and stays.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == s_main && agent.CurrentSessionId is not null), "the fallback");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
        Assert.Equal(s_agents, agent.LiveConfig!.Panel);

        agent.AgentsEndpointDown = false;
        Disconnect(config.NodeId);

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == s_agents), "the agents' address again");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
    }

    [DatabaseFact]
    public async Task WithCertificatesRequiredAnAgentFollowsTheSignedAddress()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "move-required");
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await SetRequiredAsync(admin, true);
        try
        {
            await agent.StartAsync();

            await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == s_agents), "the agent on the agents' address");
            Assert.Equal(s_main, agent.LiveConfig!.FallbackPanel);
            Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
        }
        finally
        {
            await SetRequiredAsync(admin, false);
        }
    }

    [DatabaseFact]
    public async Task TheNodeKeyRotatesOnTheAgentsAddress()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "move-rotation");
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == s_agents), "the agent on the agents' address");

        using var rotate = await admin.PostAsync($"/api/v1/nodes/{config.NodeId}/credentials/rotate");
        await AccessScenario.EnsureSuccessAsync(rotate);

        // The new key signs in with its own certificate: the rotation completes there.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.LiveConfig!.CredentialId != config.CredentialId && agent.LiveConfig.PendingRotation is null),
            "the rotation");
        // The panel ended the old key's session when the new one signed in: the node is back once the new session's
        // Hello is in, and commands reach it again.
        await InProcessAgent.EventuallyAsync(async () => agent.CurrentPanel == s_agents && await AnswersPingAsync(admin, config.NodeId), "connected again");
        Assert.True((await NodeApi.GetAsync(admin, config.NodeId)).Node.ConnectedWithCertificate);
    }

    private void Disconnect(Guid node) => factory.Services.GetRequiredService<IAgentConnections>().Disconnect(new NodeId(node));

    private static async Task<bool> AnswersPingAsync(PanelClient admin, Guid node)
    {
        using var ping = await admin.PostAsync($"/api/v1/nodes/{node}/ping");
        return ping.IsSuccessStatusCode
            && (await ping.Content.ReadFromJsonAsync<PingResult>(AccessScenario.Json, TestContext.Current.CancellationToken))!.Status == AckStatus.Accepted;
    }

    private static async Task SetRequiredAsync(PanelClient admin, bool require)
    {
        using var response = await admin.PutAsync("/api/v1/agent-transport", new UpdateAgentTransportRequest(require));
        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal(require, (await response.Content.ReadFromJsonAsync<AgentTransportOverview>(AccessScenario.Json, TestContext.Current.CancellationToken))!
            .RequireClientCertificates);
    }
}
