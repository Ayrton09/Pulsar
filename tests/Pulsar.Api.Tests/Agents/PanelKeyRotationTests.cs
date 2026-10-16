using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Infrastructure.Persistence;

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

        // Forced: nodes of the other tests may pin only the old key.
        var retired = await PostAsync(admin, $"/api/v1/panel-keys/{oldKey}/retire?force=true");

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
        await PostAsync(admin, $"/api/v1/panel-keys/{created.Enrollment.PanelKeyFingerprint}/retire?force=true");
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

    [DatabaseFact]
    public async Task ANodeThatPinsOnlyTheRetiringKeyGetsItsSessionGrantSignedWithIt()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await FinishPendingRotationAsync(admin);
        var created = await NodeApi.CreateAsync(admin, "panel-key-grant");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var oldKey = created.Enrollment.PanelKeyFingerprint;
        await PostAsync(admin, "/api/v1/panel-keys/rotate");

        // As its sign-in says: it pins the old key only.
        using (var asked = await agent.PostChallengeAsync([1, 2, 3], pinned: [oldKey]))
        {
            asked.EnsureSuccessStatusCode();
            using var response = await agent.PostTokenAsync((await asked.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!);
            response.EnsureSuccessStatusCode();
            var grant = (await response.Content.ReadFromJsonAsync<TokenResponse>(ProtocolJson.Options, Ct))!.Grant!;
            Assert.Equal(oldKey, grant.KeyId);
            Assert.True(ProtocolCrypto.VerifyMessage(grant, AgentProtocol.Purposes.SessionGrant, Assert.Single(agent.PinnedKeys, k => k.KeyId == oldKey).PublicKey));
        }

        await FinishPendingRotationAsync(admin);
    }

    [DatabaseFact]
    public async Task OnlyASignInRecordsTheKeysANodePinsAndRetiringRefusesToCutANodeOff()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await FinishPendingRotationAsync(admin);
        var created = await NodeApi.CreateAsync(admin, "panel-key-gate");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var oldKey = created.Enrollment.PanelKeyFingerprint;

        // The node signs in pinning the old key only, then goes offline; its session token leaks.
        var session = await SignInAsync(agent, [oldKey]);
        var rotated = await PostAsync(admin, "/api/v1/panel-keys/rotate");
        var newKey = Assert.Single(rotated.Keys, k => k.Status == PanelSigningKeyStatus.Active).KeyId;
        Assert.Contains("panel-key-gate", rotated.NodesToReEnroll);

        // Whoever holds the token says Hello pinning only the new key: the node's record does not change.
        await using (await HelloAsync(session.AccessToken, [newKey]))
        {
        }

        Assert.Contains("panel-key-gate", (await GetAsync(admin)).NodesToReEnroll);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var node = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().Nodes.AsNoTracking()
                .SingleAsync(n => n.Id == new NodeId(created.Node.Id), Ct);
            Assert.Equal([oldKey], node.PinnedPanelKeys);
        }

        // Retiring the old key would cut the node off: refused unless forced.
        using (var refused = await admin.PostAsync($"/api/v1/panel-keys/{Uri.EscapeDataString(oldKey)}/retire"))
        {
            await Auth.SetupTests.AssertProblemAsync(refused, HttpStatusCode.Conflict, "panel_key.nodes_outdated");
        }

        // Keys the node key did not sign are refused; a sign-in that signs both records them.
        using (var asked = await agent.PostChallengeAsync(RandomNumberGenerator.GetBytes(32), pinned: [oldKey]))
        {
            var challenge = (await asked.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!;
            using var forged = await agent.PostTokenAsync(challenge, pinned: [oldKey, newKey]);
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }

        await SignInAsync(agent, [oldKey, newKey]);
        Assert.DoesNotContain("panel-key-gate", (await GetAsync(admin)).NodesToReEnroll);
        await FinishPendingRotationAsync(admin);
    }

    [DatabaseFact]
    public async Task AKeyRetiredWhileThePanelReadsItsKeysIsNotKept()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await FinishPendingRotationAsync(admin);
        var retiring = Assert.Single((await PostAsync(admin, "/api/v1/panel-keys/rotate")).Keys, k => k.Status == PanelSigningKeyStatus.Retiring).KeyId;
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        var ring = app.Services.GetRequiredService<IPanelKeyRing>();

        // The retirement commits and tells the ring while the ring reads the keys: what it read is from before.
        hook.AfterQuery("panel_signing_keys", async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            (await db.PanelSigningKeys.SingleAsync(k => k.KeyId == retiring, CancellationToken.None)).Retire(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
            ring.Reload();
        });
        ring.Reload();
        await ring.GetPublicKeysAsync(Ct);

        Assert.DoesNotContain(retiring, (await ring.GetPublicKeysAsync(Ct)).Select(k => k.KeyId));

        // The panel the other tests share read the keys before the retirement, which bypassed it: it reads them again.
        factory.Services.GetRequiredService<IPanelKeyRing>().Reload();
    }

    private static HashSet<string> Pins(InProcessAgent agent) => agent.LiveConfig is { } config ? Pins(config) : [];

    private static HashSet<string> Pins(Agent.Core.AgentConfig config) => [.. config.PanelKeys.Select(k => k.KeyId)];

    /// <summary>The tests share the panel: each starts with no key being retired.</summary>
    private static async Task FinishPendingRotationAsync(PanelClient admin)
    {
        foreach (var key in (await GetAsync(admin)).Keys.Where(k => k.Status == PanelSigningKeyStatus.Retiring))
        {
            // Escaped as the browser client sends it ("sha256%3A...").
            await PostAsync(admin, $"/api/v1/panel-keys/{Uri.EscapeDataString(key.KeyId)}/retire?force=true");
        }
    }

    private static async Task<TokenResponse> SignInAsync(TestAgent agent, IReadOnlyList<string> pinned)
    {
        using var asked = await agent.PostChallengeAsync(RandomNumberGenerator.GetBytes(32), pinned: pinned);
        asked.EnsureSuccessStatusCode();
        using var response = await agent.PostTokenAsync((await asked.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(ProtocolJson.Options, Ct))!;
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
        await connection.StartAsync(Ct);
        await connection.InvokeAsync<HelloAccepted>(nameof(IAgentHub.Hello),
            new AgentHello("0.1.0-thief", AgentProtocol.CurrentVersion, [], TestAgent.Inventory, pinned), Ct);
        return connection;
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
