using System.Net;
using System.Net.Http.Json;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Nodes;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// Panel signing key rotation (docs/architecture/03): agents take the new key from a command signed by a key they
/// already trust, whether they are connected at the time or connect later; the old key is retired afterwards.
/// </summary>
public sealed class PanelKeyRotationTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ConnectedAgentsTakeTheNewKeyAndDropTheRetiredOne()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await FinishPendingRotationAsync(admin);
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "panel-key-online");
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, config.NodeId)).Node.PanelKeyTrust == PanelKeyTrust.Current, "connection");
        var oldKey = created.Enrollment.PanelKeyFingerprint;

        var rotated = await PostAsync(admin, "/api/v1/panel-keys/rotate");

        var newKey = Assert.Single(rotated.Keys, k => k.Status == PanelSigningKeyStatus.Active).KeyId;
        Assert.Equal(PanelSigningKeyStatus.Retiring, Assert.Single(rotated.Keys, k => k.KeyId == oldKey).Status);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Pins(agent).SetEquals([oldKey, newKey])), "the agent pinning both keys");
        // Commands are now signed with the new key, which the agent accepts.
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
        Assert.Equal(PanelKeyTrust.Current, (await NodeApi.GetAsync(admin, config.NodeId)).Node.PanelKeyTrust);
        Assert.True(Pins(agent.Configs.Load()!).SetEquals([oldKey, newKey])); // saved, so it survives a restart

        var retired = await PostAsync(admin, $"/api/v1/panel-keys/{oldKey}/retire");

        Assert.Equal(PanelSigningKeyStatus.Retired, Assert.Single(retired.Keys, k => k.KeyId == oldKey).Status);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Pins(agent).SetEquals([newKey])), "the agent dropping the retired key");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
        using var identity = await factory.CreateDefaultClient(new Uri("https://localhost")).GetAsync(new Uri(AgentProtocol.PanelIdentityPath, UriKind.Relative), Ct);
        Assert.Equal([newKey], (await identity.Content.ReadFromJsonAsync<PanelIdentityResponse>(ProtocolJson.Options, Ct))!.Keys.Select(k => k.KeyId));
    }

    [DatabaseFact]
    public async Task AnAgentOfflineDuringTheRotationGetsTheNewKeyWhenItConnects()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await FinishPendingRotationAsync(admin);
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "panel-key-offline");
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);

        var rotated = await PostAsync(admin, "/api/v1/panel-keys/rotate");
        var newKey = Assert.Single(rotated.Keys, k => k.Status == PanelSigningKeyStatus.Active).KeyId;
        await agent.StartAsync();

        // It pins only the old key: the panel signs with it, and sends the new list.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Pins(agent).Contains(newKey)), "the agent taking the new key");
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, config.NodeId)).Node.PanelKeyTrust == PanelKeyTrust.Current, "trust recorded");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, config.NodeId)).Status);
        await FinishPendingRotationAsync(admin);
    }

    [DatabaseFact]
    public async Task AnAgentThatMissedTheWholeRotationIsReportedAndCannotBeCommanded()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await FinishPendingRotationAsync(admin);
        await using var agent = new InProcessAgent(factory);
        var created = await NodeApi.CreateAsync(admin, "panel-key-missed");
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);

        await PostAsync(admin, "/api/v1/panel-keys/rotate");
        await PostAsync(admin, $"/api/v1/panel-keys/{created.Enrollment.PanelKeyFingerprint}/retire");
        await agent.StartAsync();

        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, config.NodeId)).Node.PanelKeyTrust == PanelKeyTrust.Outdated, "the node reported as outdated");
        Assert.True((await GetAsync(admin)).NodesOutdated >= 1);
        // No key it trusts can sign a command: the node has to be enrolled again.
        using var ping = await admin.PostAsync($"/api/v1/nodes/{config.NodeId}/ping");
        await Auth.SetupTests.AssertProblemAsync(ping, HttpStatusCode.Conflict, "node.not_connected");
        Assert.Equal([created.Enrollment.PanelKeyFingerprint], agent.LiveConfig!.PanelKeys.Select(k => k.KeyId));
    }

    [DatabaseFact]
    public async Task OnlyOneRotationAtATimeAndOnlyRetiringKeysCanBeRetired()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await FinishPendingRotationAsync(admin);
        var active = Assert.Single((await GetAsync(admin)).Keys, k => k.Status == PanelSigningKeyStatus.Active).KeyId;

        using (var notRetiring = await admin.PostAsync($"/api/v1/panel-keys/{active}/retire"))
        {
            await Auth.SetupTests.AssertProblemAsync(notRetiring, HttpStatusCode.Conflict, "panel_key.not_retiring");
        }

        await PostAsync(admin, "/api/v1/panel-keys/rotate");
        using (var again = await admin.PostAsync("/api/v1/panel-keys/rotate"))
        {
            await Auth.SetupTests.AssertProblemAsync(again, HttpStatusCode.Conflict, "panel_key.rotation_in_progress");
        }

        using (var unknown = await admin.PostAsync("/api/v1/panel-keys/sha256:unknown/retire"))
        {
            await Auth.SetupTests.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "panel_key.not_found");
        }

        await FinishPendingRotationAsync(admin);
    }

    private static HashSet<string> Pins(InProcessAgent agent) => agent.LiveConfig is { } config ? Pins(config) : [];

    private static HashSet<string> Pins(Agent.Core.AgentConfig config) => [.. config.PanelKeys.Select(k => k.KeyId)];

    /// <summary>The tests share the panel: each starts with no key being retired.</summary>
    private static async Task FinishPendingRotationAsync(PanelClient admin)
    {
        foreach (var key in (await GetAsync(admin)).Keys.Where(k => k.Status == PanelSigningKeyStatus.Retiring))
        {
            // Escaped as the browser client sends it ("sha256%3A...").
            await PostAsync(admin, $"/api/v1/panel-keys/{Uri.EscapeDataString(key.KeyId)}/retire");
        }
    }

    private static async Task<PanelKeysOverview> GetAsync(PanelClient admin) =>
        (await admin.Http.GetFromJsonAsync<PanelKeysOverview>(new Uri("/api/v1/panel-keys", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static async Task<PanelKeysOverview> PostAsync(PanelClient admin, string path)
    {
        using var response = await admin.PostAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<PanelKeysOverview>(AccessScenario.Json, Ct))!;
    }
}
