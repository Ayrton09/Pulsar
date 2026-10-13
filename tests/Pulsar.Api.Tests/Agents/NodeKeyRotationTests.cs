using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Agents;

/// <summary>Node key rotation with the real agent core (docs/architecture/03, "Rotation").</summary>
public sealed class NodeKeyRotationTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ThePanelCanAskAnAgentToReplaceItsKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-on-request");
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, enrolled.NodeId)).Node.Connected, "connection");

        using (var request = await admin.PostAsync($"/api/v1/nodes/{enrolled.NodeId}/credentials/rotate"))
        {
            await AccessScenario.EnsureSuccessAsync(request);
            Assert.Equal(AckStatus.Accepted, (await request.Content.ReadFromJsonAsync<AgentCommandResult>(AccessScenario.Json, Ct))!.Status);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.LiveConfig is { PendingRotation: null } c && c.CredentialId != enrolled.CredentialId),
            "the agent switching to its new credential");
        var rotated = agent.LiveConfig!;
        await AssertRotatedAsync(admin, agent, enrolled, rotated);
        var saved = agent.Configs.Load()!;
        Assert.Equal((rotated.CredentialId, rotated.Key, rotated.KeyCreatedAt), (saved.CredentialId, saved.Key, saved.KeyCreatedAt));
        Assert.Null(saved.PendingRotation);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var nodeId = new NodeId(enrolled.NodeId);
        foreach (var action in new[] { AuditActions.Node.CredentialRotationRequested, AuditActions.Node.CredentialRotationStarted, AuditActions.Node.CredentialRotated })
        {
            Assert.True(await db.AuditLog.AnyAsync(e => e.Action == action && e.NodeId == nodeId, Ct), action);
        }
    }

    [DatabaseFact]
    public async Task KeysOlderThanTheRotationIntervalAreReplacedAutomatically()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-by-age");
        agent.Configs.Save(enrolled with { KeyCreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(91) });

        await agent.StartAsync();

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.LiveConfig is { PendingRotation: null } c && c.CredentialId != enrolled.CredentialId),
            "an automatic rotation");
        Assert.True(DateTimeOffset.UtcNow - agent.LiveConfig!.CurrentKeyCreatedAt < TimeSpan.FromMinutes(1));
        await AssertRotatedAsync(admin, agent, enrolled, agent.LiveConfig);
    }

    [DatabaseFact]
    public async Task AKeyTheMachineCannotCreateLeavesTheSessionAndTheCurrentKeyAsTheyWere()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-no-new-key");
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, enrolled.NodeId)).Node.Connected, "connection");
        var session = agent.CurrentSessionId;
        agent.Keys.RefuseCreate = true;

        using (var request = await admin.PostAsync($"/api/v1/nodes/{enrolled.NodeId}/credentials/rotate"))
        {
            await AccessScenario.EnsureSuccessAsync(request);
        }

        // A failure that dropped the connection would reconnect within the test's backoff (two seconds at most).
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(session, agent.CurrentSessionId);
        Assert.Equal(enrolled.CredentialId, agent.LiveConfig!.CredentialId);
        Assert.Null(agent.LiveConfig.PendingRotation);
        Assert.Equal([enrolled.Key.Name], agent.Keys.Names);
    }

    [DatabaseFact]
    public async Task StoppingTheAgentWhileItRegistersANewKeyLeavesNoKeyBehind()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-then-stop");
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, enrolled.NodeId)).Node.Connected, "connection");
        using var hold = new ManualResetEventSlim();
        agent.Keys.HoldSigning = hold;

        using (var request = await admin.PostAsync($"/api/v1/nodes/{enrolled.NodeId}/credentials/rotate"))
        {
            await AccessScenario.EnsureSuccessAsync(request);
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.Keys.SigningHeld.IsSet), "the new key about to be registered");
        var stopping = agent.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(1), Ct); // the service stop cancels the registration meanwhile
        hold.Set();
        await stopping;

        Assert.Equal([enrolled.Key.Name], agent.Keys.Names);
        Assert.Null(agent.Configs.Load()!.PendingRotation);
    }

    [DatabaseFact]
    public async Task ARotationInterruptedBeforeItsFirstLoginCompletesWhenTheAgentStartsAgain()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-after-crash");

        // As if the agent stopped right after the panel registered the new key.
        var pending = await RegisterPendingKeyAsync(agent, enrolled);
        agent.Configs.Save(enrolled with { PendingRotation = pending });
        await AssertCredentialStatusAsync(admin, enrolled.NodeId, pending.CredentialId, NodeCredentialStatus.Pending);

        await agent.StartAsync();

        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, enrolled.NodeId)).Node.Connected, "connection");
        Assert.Equal(pending.CredentialId, agent.LiveConfig!.CredentialId);
        await AssertRotatedAsync(admin, agent, enrolled, agent.LiveConfig);
    }

    [DatabaseFact]
    public async Task APendingKeyThePanelNoLongerAcceptsIsAbandonedForTheCurrentOne()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-abandoned");
        var abandoned = await RegisterPendingKeyAsync(agent, enrolled);
        await RegisterPendingKeyAsync(agent, enrolled); // a later rotation supersedes the first pending key
        agent.Configs.Save(enrolled with { PendingRotation = abandoned });

        await agent.StartAsync();

        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, enrolled.NodeId)).Node.Connected, "connection with the current key");
        Assert.Equal(enrolled.CredentialId, agent.LiveConfig!.CredentialId);
        Assert.Null(agent.LiveConfig.PendingRotation);
        Assert.DoesNotContain(abandoned.Key.Name, agent.Keys.Names);
        await AssertCredentialStatusAsync(admin, enrolled.NodeId, abandoned.CredentialId, NodeCredentialStatus.Superseded);
    }

    [DatabaseFact]
    public async Task AnAbandonedRotationIsTriedAgainOnlyAfterItsRetryDelay()
    {
        // A key that is due by age, and new keys this machine cannot open again: each rotation is abandoned at the sign-in
        // with its key. The next one waits, rather than making and registering keys in a loop.
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-retry-delay");
        agent.Configs.Save(enrolled with { KeyCreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(91) });
        agent.Keys.LoseCreatedKeys = true;
        var enrolledWith = agent.Keys.Created;

        await agent.StartAsync();

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.Keys.Created > enrolledWith && agent.LiveConfig is { PendingRotation: null }),
            "a rotation, abandoned");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal(enrolledWith + 1, agent.Keys.Created);
        Assert.Equal(enrolled.CredentialId, agent.LiveConfig!.CredentialId);
        Assert.Equal([enrolled.Key.Name], agent.Keys.Names);
        Assert.True((await NodeApi.GetAsync(admin, enrolled.NodeId)).Node.Connected);
    }

    [DatabaseFact]
    public async Task ReEnrollingRevokesAPendingKeyToo()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-then-reenroll");
        var pending = await RegisterPendingKeyAsync(agent, enrolled);

        using var tokenResponse = await admin.PostAsync($"/api/v1/nodes/{enrolled.NodeId}/enrollment-tokens");
        await AccessScenario.EnsureSuccessAsync(tokenResponse);
        var instructions = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentInstructions>(AccessScenario.Json, Ct))!;
        var reenrolled = await agent.EnrollAsync(instructions.Token, instructions.PanelKeyFingerprint);

        // Neither earlier key works any more: the pending one could otherwise sign in and supersede the new key.
        await AssertCredentialStatusAsync(admin, enrolled.NodeId, enrolled.CredentialId, NodeCredentialStatus.Revoked);
        await AssertCredentialStatusAsync(admin, enrolled.NodeId, pending.CredentialId, NodeCredentialStatus.Revoked);
        await AssertCredentialStatusAsync(admin, enrolled.NodeId, reenrolled.CredentialId, NodeCredentialStatus.Active);
    }

    [DatabaseFact]
    public async Task TheNewKeyMustProveItselfAndTheRequestMustComeFromASession()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-proof");
        using var current = agent.Keys.Open(enrolled.Key);
        var session = await new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled, current, Ct);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = newKey.ExportSubjectPublicKeyInfo();
        var forgedProof = ProtocolCrypto.Sign(impostor, ProtocolCrypto.SignedData(AgentProtocol.Purposes.RotateCredential,
            ProtocolCrypto.GuidBytes(enrolled.NodeId), ProtocolCrypto.GuidBytes(enrolled.CredentialId), ProtocolCrypto.GuidBytes(session.SessionId), publicKey));
        using var http = factory.CreateDefaultClient(new Uri("https://localhost"));

        using (var anonymous = await http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, new RotateCredentialRequest(publicKey, "software", forgedProof), ProtocolJson.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var forged = await http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, new RotateCredentialRequest(publicKey, "software", forgedProof), ProtocolJson.Options, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("agent.rotation_rejected", (await forged.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, Ct))!.Code);
        Assert.Single((await NodeApi.GetAsync(admin, enrolled.NodeId)).Credentials);
    }

    [DatabaseFact]
    public async Task ThePanelsAnswerNamesTheKeyItRegistered()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-bound");
        using var current = agent.Keys.Open(enrolled.Key);
        var session = await new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled, current, Ct);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = newKey.ExportSubjectPublicKeyInfo();
        var proof = ProtocolCrypto.Sign(newKey, ProtocolCrypto.SignedData(AgentProtocol.Purposes.RotateCredential,
            ProtocolCrypto.GuidBytes(enrolled.NodeId), ProtocolCrypto.GuidBytes(enrolled.CredentialId), ProtocolCrypto.GuidBytes(session.SessionId), publicKey));
        using var http = factory.CreateDefaultClient(new Uri("https://localhost"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        using var response = await http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, new RotateCredentialRequest(publicKey, "software", proof),
            ProtocolJson.Options, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signed = (await response.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!;
        var rotation = ProtocolJson.Deserialize<CredentialRotation>(signed.Payload);
        Assert.Equal(ProtocolCrypto.Fingerprint(publicKey), rotation.KeyFingerprint);
    }

    private static async Task AssertRotatedAsync(PanelClient admin, InProcessAgent agent, AgentConfig before, AgentConfig after)
    {
        var details = await NodeApi.GetAsync(admin, before.NodeId);
        var active = Assert.Single(details.Credentials, c => c.Status == NodeCredentialStatus.Active);
        Assert.Equal(after.CredentialId, active.Id);
        using (var key = agent.Keys.Open(after.Key))
        {
            Assert.Equal(ProtocolCrypto.Fingerprint(key.PublicKey), active.Fingerprint);
        }

        var old = Assert.Single(details.Credentials, c => c.Id == before.CredentialId);
        Assert.Equal(NodeCredentialStatus.Superseded, old.Status);
        Assert.NotNull(old.SupersededAt);
        Assert.Equal([after.Key.Name], agent.Keys.Names);
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, before.NodeId)).Node.Connected, "connection with the new key");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, before.NodeId)).Status);
    }

    private static async Task AssertCredentialStatusAsync(PanelClient admin, Guid nodeId, Guid credentialId, NodeCredentialStatus status) =>
        Assert.Equal(status, Assert.Single((await NodeApi.GetAsync(admin, nodeId)).Credentials, c => c.Id == credentialId).Status);

    /// <summary>Step 1 of a rotation, done by hand: a session with the current key registers a new one.</summary>
    private static async Task<PendingRotation> RegisterPendingKeyAsync(InProcessAgent agent, AgentConfig config)
    {
        using var current = agent.Keys.Open(config.Key);
        var transport = Transport(agent);
        var session = await new PanelAuthenticator(transport).AuthenticateAsync(config, current, Ct);
        using var newKey = agent.Keys.Create($"PulsarAgent-{Guid.NewGuid():N}", NodeKeyScope.User);
        var credentialId = await new CredentialRotationClient(transport).RegisterAsync(config, config.Panel, null, session.AccessToken, session.SessionId, newKey, Ct);
        return new PendingRotation(newKey.Reference, credentialId, DateTimeOffset.UtcNow);
    }

    private static PanelTransport Transport(InProcessAgent agent) => agent.Transport;

    private static async Task<AgentConfig> EnrollAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await NodeApi.CreateAsync(admin, name);
        return await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
    }
}
