using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Nodes;

/// <summary>The ports any program uses on a node's machine (1.0.7), which the create dialog skips when it proposes ports.</summary>
public sealed class PortsInUseTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TheAgentListsWhatThisMachineListensOnAndHasBound()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "ports-in-use");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "connected");
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        var ports = await GetAsync(admin, created.Node.Id);

        Assert.True(ports.Known);
        Assert.Contains(new PortInUse("127.0.0.1", ((IPEndPoint)tcp.LocalEndpoint).Port, "tcp"), ports.Ports);
        Assert.Contains(new PortInUse("127.0.0.1", ((IPEndPoint)udp.Client.LocalEndPoint!).Port, "udp"), ports.Ports);
    }

    [DatabaseFact]
    public async Task AnAgentThatCannotSayIsNotTakenForAnEmptyMachine()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "ports-older-agent");

        // Not connected yet: nothing is known.
        Assert.False((await GetAsync(admin, created.Node.Id)).Known);

        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, AgentCapabilities.Ping);

        var ports = await GetAsync(admin, created.Node.Id);

        Assert.False(ports.Known);
        Assert.Empty(ports.Ports);
        Assert.DoesNotContain(agent.ReceivedCommands, c => c.Command is ListPortsInUseCommand);
    }

    [DatabaseFact]
    public async Task OnlyThoseWhoSeeTheNodeSeeThem()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var created = await NodeApi.CreateAsync(scenario.Admin, "ports-hidden");
        using var nobody = await scenario.SignedInWithAsync("ports-nobody");
        using var viewer = await scenario.SignedInWithAsync("ports-server-viewer", "server.view");
        // They choose among the node's allocations; what else runs on the machine is the node's business.
        using var creator = await scenario.SignedInWithAsync("ports-server-creator", "server.create");

        foreach (var client in new[] { nobody, viewer, creator })
        {
            using var response = await client.GetAsync($"/api/v1/nodes/{created.Node.Id}/ports-in-use");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [DatabaseFact]
    public async Task EachNodeIsAskedAtMostSoManyTimesAMinute()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "ports-limited");
        var other = await NodeApi.CreateAsync(admin, "ports-not-limited");

        for (var i = 0; i < Pulsar.Api.Nodes.NodeEndpoints.PortsInUsePermitLimit; i++)
        {
            await GetAsync(admin, created.Node.Id);
        }

        using var refused = await admin.GetAsync($"/api/v1/nodes/{created.Node.Id}/ports-in-use");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.False((await GetAsync(admin, other.Node.Id)).Known);
    }

    private static async Task<NodePortsInUse> GetAsync(PanelClient client, Guid nodeId)
    {
        using var response = await client.GetAsync($"/api/v1/nodes/{nodeId}/ports-in-use");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<NodePortsInUse>(AccessScenario.Json, Ct))!;
    }
}
