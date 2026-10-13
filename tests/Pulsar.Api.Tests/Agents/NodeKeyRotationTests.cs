using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Api.Nodes;
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
    public async Task RevokingTheActiveKeyRevokesAPendingKeyToo()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-then-revoke");
        // Whoever holds the node key (a thief, say) registers a key of their own before the administrator revokes it.
        var pending = await RegisterPendingKeyAsync(agent, enrolled);

        using (var revoke = await admin.PostAsync($"/api/v1/nodes/{enrolled.NodeId}/credentials/{enrolled.CredentialId}/revoke",
            new RevokeCredentialRequest("Key leaked")))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        await AssertCredentialStatusAsync(admin, enrolled.NodeId, pending.CredentialId, NodeCredentialStatus.Revoked);
        using var key = agent.Keys.Open(pending.Key);
        await Assert.ThrowsAsync<AgentRejectedException>(() =>
            new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled with { CredentialId = pending.CredentialId, Key = pending.Key }, key, Ct));
    }

    [DatabaseFact]
    public async Task ARotationRacingARevocationLeavesNoKeyBehind()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-while-revoking");
        using var current = agent.Keys.Open(enrolled.Key);
        var session = await new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled, current, Ct);
        using var newKey = agent.Keys.Create($"PulsarAgent-{Guid.NewGuid():N}", NodeKeyScope.User);
        var nodeId = new NodeId(enrolled.NodeId);

        // A revocation in progress, as the panel makes it: the node's row locked, the key revoked, not committed yet.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM nodes WHERE id = {enrolled.NodeId} FOR UPDATE", Ct);
            var credential = await db.NodeCredentials.SingleAsync(c => c.Id == enrolled.CredentialId, Ct);
            credential.Revoke(null, "Key leaked", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);

            var rotating = new CredentialRotationClient(Transport(agent))
                .RegisterAsync(enrolled, enrolled.Panel, null, session.AccessToken, session.SessionId, current, newKey, Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct); // the rotation is now waiting for the revocation, or past it
            await transaction.CommitAsync(Ct);
            await Assert.ThrowsAnyAsync<Exception>(() => rotating);
        }

        Assert.False(await db.NodeCredentials.AnyAsync(c => c.NodeId == nodeId && c.Status == NodeCredentialStatus.Pending, Ct));
    }

    [DatabaseFact]
    public async Task ARotationRacingAReEnrollmentLeavesNoKeyBehind()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-while-reenrolling");
        using var current = agent.Keys.Open(enrolled.Key);
        var session = await new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled, current, Ct);
        using var newKey = agent.Keys.Create($"PulsarAgent-{Guid.NewGuid():N}", NodeKeyScope.User);
        var nodeId = new NodeId(enrolled.NodeId);
        using var tokenResponse = await admin.PostAsync($"/api/v1/nodes/{enrolled.NodeId}/enrollment-tokens");
        await AccessScenario.EnsureSuccessAsync(tokenResponse);
        var instructions = (await tokenResponse.Content.ReadFromJsonAsync<EnrollmentInstructions>(AccessScenario.Json, Ct))!;
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        await using var reinstalled = new InProcessAgent(app);

        // The re-enrollment has read the node's keys, and not saved yet, when the old key's session registers a new one.
        Task? rotating = null;
        hook.BeforeSave(c => c.ChangeTracker.Entries<NodeEnrollmentToken>().Any(e => e.Entity.NodeId == nodeId), async () =>
        {
            rotating = new CredentialRotationClient(Transport(agent))
                .RegisterAsync(enrolled, enrolled.Panel, null, session.AccessToken, session.SessionId, current, newKey, Ct);
            await Task.WhenAny(rotating, Task.Delay(TimeSpan.FromSeconds(1), Ct)); // registered, or waiting for the re-enrollment
        });
        await reinstalled.EnrollAsync(instructions.Token, instructions.PanelKeyFingerprint);
        var refused = await Record.ExceptionAsync(() => rotating!);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        Assert.False(await db.NodeCredentials.AnyAsync(c => c.NodeId == nodeId && c.Status == NodeCredentialStatus.Pending, Ct));
        Assert.NotNull(refused);
    }

    [DatabaseFact]
    public async Task APendingKeyRevokedWhileItSignsInIsNotActivated()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        await using var agent = new InProcessAgent(app);
        var enrolled = await EnrollAsync(admin, agent, "rotate-revoked-at-sign-in");
        var pending = await RegisterPendingKeyAsync(agent, enrolled);
        var nodeId = new NodeId(enrolled.NodeId);

        // Revoked once the sign-in with the pending key has read it, before the rotation completes.
        hook.AfterQuery("challenge_CredentialId", async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<NodeManagement>()
                .RevokeCredentialAsync(nodeId, pending.CredentialId, "Not ours", CancellationToken.None)).IsSuccess);
        });
        using (var key = agent.Keys.Open(pending.Key))
        {
            await Assert.ThrowsAsync<AgentRejectedException>(() =>
                new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled with { CredentialId = pending.CredentialId, Key = pending.Key }, key, Ct));
        }

        await AssertCredentialStatusAsync(admin, enrolled.NodeId, pending.CredentialId, NodeCredentialStatus.Revoked);
        await AssertCredentialStatusAsync(admin, enrolled.NodeId, enrolled.CredentialId, NodeCredentialStatus.Active);
        await using var check = factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var refusals = await db.AuditLog.Where(e => e.Action == AuditActions.Node.AuthenticationFailed && e.NodeId == nodeId).ToListAsync(Ct);
        Assert.Contains(refusals, e => e.Metadata.Contains("\"inactive_while_signing_in\"", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task TheNewKeysFirstSignInClosesTheOldKeysConnection()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "rotate-closes-old");
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        var session = await agent.AuthenticateAsync();
        await agent.ConnectAsync(session.AccessToken, AgentCapabilities.Ping);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = newKey.ExportSubjectPublicKeyInfo();
        var proof = ProtocolCrypto.Sign(newKey, RotationData(AgentProtocol.Purposes.RotateCredential, agent.NodeId, agent.CredentialId, session.SessionId, publicKey));
        var authorization = ProtocolCrypto.Sign(agent.Key,
            RotationData(AgentProtocol.Purposes.AuthorizeRotation, agent.NodeId, agent.CredentialId, session.SessionId, publicKey));
        agent.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        Guid pending;
        using (var rotated = await agent.Http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, new RotateCredentialRequest(publicKey, "software", proof, authorization),
            ProtocolJson.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            pending = ProtocolJson.Deserialize<CredentialRotation>((await rotated.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!.Payload)
                .CredentialId;
        }

        agent.Http.DefaultRequestHeaders.Authorization = null;
        agent.UseCredential(pending);
        using (var signedIn = await agent.PostTokenAsync(await agent.ChallengeAsync(), newKey))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        // The old key's session ended, and the connection it opened goes with it: it could otherwise still report.
        await agent.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
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
        var forgedProof = ProtocolCrypto.Sign(impostor,
            RotationData(AgentProtocol.Purposes.RotateCredential, enrolled.NodeId, enrolled.CredentialId, session.SessionId, publicKey));
        var authorization = current.Sign(RotationData(AgentProtocol.Purposes.AuthorizeRotation, enrolled.NodeId, enrolled.CredentialId, session.SessionId, publicKey));
        var request = new RotateCredentialRequest(publicKey, "software", forgedProof, authorization);
        using var http = factory.CreateDefaultClient(new Uri("https://localhost"));

        using (var anonymous = await http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, request, ProtocolJson.Options, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var forged = await http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, request, ProtocolJson.Options, Ct);

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
        var proof = ProtocolCrypto.Sign(newKey, RotationData(AgentProtocol.Purposes.RotateCredential, enrolled.NodeId, enrolled.CredentialId, session.SessionId, publicKey));
        var authorization = current.Sign(RotationData(AgentProtocol.Purposes.AuthorizeRotation, enrolled.NodeId, enrolled.CredentialId, session.SessionId, publicKey));
        using var http = factory.CreateDefaultClient(new Uri("https://localhost"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        using var response = await http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, new RotateCredentialRequest(publicKey, "software", proof, authorization),
            ProtocolJson.Options, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var signed = (await response.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!;
        var rotation = ProtocolJson.Deserialize<CredentialRotation>(signed.Payload);
        Assert.Equal(ProtocolCrypto.Fingerprint(publicKey), rotation.KeyFingerprint);
    }

    [DatabaseFact]
    public async Task ACopiedSessionTokenCannotRegisterAKeyWithoutTheCurrentKey()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-stolen-token");
        using var current = agent.Keys.Open(enrolled.Key);
        var session = await new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled, current, Ct);

        // Whoever copied the token also sees the ids (a proxy that decrypts the traffic), and proves a key of their own;
        // the current key's authorization is what they cannot make.
        using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = thief.ExportSubjectPublicKeyInfo();
        var proof = ProtocolCrypto.Sign(thief, RotationData(AgentProtocol.Purposes.RotateCredential, enrolled.NodeId, enrolled.CredentialId, session.SessionId, publicKey));
        var signedByThief = ProtocolCrypto.Sign(thief,
            RotationData(AgentProtocol.Purposes.AuthorizeRotation, enrolled.NodeId, enrolled.CredentialId, session.SessionId, publicKey));
        // The current key's signature over the same fields for another purpose: not an authorization.
        var otherPurpose = current.Sign(RotationData(AgentProtocol.Purposes.RotateCredential, enrolled.NodeId, enrolled.CredentialId, session.SessionId, publicKey));
        // The current key's authorization of this key for another session (one it signed earlier, played back).
        var otherSession = current.Sign(RotationData(AgentProtocol.Purposes.AuthorizeRotation, enrolled.NodeId, enrolled.CredentialId, Guid.NewGuid(), publicKey));
        using var http = factory.CreateDefaultClient(new Uri("https://localhost"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        foreach (var request in new object[]
        {
            new { publicKey, keyStorage = "software", proof },
            new RotateCredentialRequest(publicKey, "software", proof, signedByThief),
            new RotateCredentialRequest(publicKey, "software", proof, otherPurpose),
            new RotateCredentialRequest(publicKey, "software", proof, otherSession),
        })
        {
            using var refused = await http.PostAsJsonAsync(AgentProtocol.RotateCredentialPath, request, ProtocolJson.Options, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("agent.rotation_rejected", (await refused.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, Ct))!.Code);
        }

        Assert.Single((await NodeApi.GetAsync(admin, enrolled.NodeId)).Credentials);
    }

    [DatabaseFact]
    public async Task APendingKeyNotUsedWithinADayIsRefusedAndSuperseded()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new InProcessAgent(factory);
        var enrolled = await EnrollAsync(admin, agent, "rotate-pending-expired");
        var pending = await RegisterPendingKeyAsync(agent, enrolled);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var registeredAt = DateTimeOffset.UtcNow - NodeCredential.PendingLifetime - TimeSpan.FromMinutes(1);
            await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().Database
                .ExecuteSqlAsync($"UPDATE node_credentials SET created_at = {registeredAt} WHERE id = {pending.CredentialId}", Ct);
        }

        using (var key = agent.Keys.Open(pending.Key))
        {
            await Assert.ThrowsAsync<AgentRejectedException>(() =>
                new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled with { CredentialId = pending.CredentialId, Key = pending.Key }, key, Ct));
        }

        await AssertCredentialStatusAsync(admin, enrolled.NodeId, pending.CredentialId, NodeCredentialStatus.Superseded);
        await AssertCredentialStatusAsync(admin, enrolled.NodeId, enrolled.CredentialId, NodeCredentialStatus.Active);
        using (var current = agent.Keys.Open(enrolled.Key))
        {
            await new PanelAuthenticator(Transport(agent)).AuthenticateAsync(enrolled, current, Ct);
        }

        await using var check = factory.Services.CreateAsyncScope();
        var nodeId = new NodeId(enrolled.NodeId);
        var refusals = await check.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog
            .Where(e => e.Action == AuditActions.Node.AuthenticationFailed && e.NodeId == nodeId).ToListAsync(Ct);
        Assert.Contains(refusals, e => e.Metadata.Contains("\"pending_credential_expired\"", StringComparison.Ordinal));
    }

    private static byte[] RotationData(string purpose, Guid nodeId, Guid credentialId, Guid sessionId, byte[] publicKey) =>
        ProtocolCrypto.SignedData(purpose, ProtocolCrypto.GuidBytes(nodeId), ProtocolCrypto.GuidBytes(credentialId), ProtocolCrypto.GuidBytes(sessionId), publicKey);

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
        var credentialId = await new CredentialRotationClient(transport).RegisterAsync(config, config.Panel, null, session.AccessToken, session.SessionId, current, newKey, Ct);
        return new PendingRotation(newKey.Reference, credentialId, DateTimeOffset.UtcNow);
    }

    private static PanelTransport Transport(InProcessAgent agent) => agent.Transport;

    private static async Task<AgentConfig> EnrollAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await NodeApi.CreateAsync(admin, name);
        return await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
    }
}
