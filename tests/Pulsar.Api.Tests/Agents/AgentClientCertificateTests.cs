using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Nodes;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// Client certificates (Phase 16, docs/architecture/03): on the agents' endpoint an agent presents its node key as a
/// TLS client certificate and its session is bound to it; the main endpoint can be closed to agents without one.
/// </summary>
public sealed class AgentClientCertificateTests(AgentsEndpointApiFactory factory) : IClassFixture<AgentsEndpointApiFactory>
{
    private const string AgentsUrl = "https://localhost:8444";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task OnTheAgentsEndpointAnAgentSignsInWithItsNodeKeyAsCertificate()
    {
        await using var agent = await EnrolledAgentAsync("certificate-sign-in");
        agent.ThroughAgentsEndpoint = true;

        using var someone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        agent.ClientCertificate = TestAgent.CertificateFor(someone);
        using (var stolen = await agent.PostChallengeAsync())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
        }

        agent.ClientCertificate = null;
        using (var none = await agent.PostChallengeAsync())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        }

        var token = await agent.AuthenticateWithCertificateAsync();
        Assert.False(string.IsNullOrEmpty(token.AccessToken));
    }

    [DatabaseFact]
    public async Task ASessionBoundToTheCertificateIsRefusedWithoutIt()
    {
        await using var agent = await EnrolledAgentAsync("certificate-bound");
        var token = await agent.AuthenticateWithCertificateAsync();

        // An unknown ticket is 404 once the session is accepted, 401 when it is not.
        Assert.Equal(HttpStatusCode.NotFound, await TransferStatusAsync(agent, token));

        agent.ThroughAgentsEndpoint = false;
        agent.ClientCertificate = null;
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferStatusAsync(agent, token));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        agent.ThroughAgentsEndpoint = true;
        agent.ClientCertificate = TestAgent.CertificateFor(other);
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferStatusAsync(agent, token));

        // Rotating the key within the session is no exception.
        agent.ThroughAgentsEndpoint = false;
        agent.ClientCertificate = null;
        using var rotate = new HttpRequestMessage(HttpMethod.Post, AgentProtocol.RotateCredentialPath)
        {
            Content = JsonContent.Create(new RotateCredentialRequest([1], "software", [2]), options: ProtocolJson.Options),
        };
        rotate.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var rotated = await agent.Http.SendAsync(rotate, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
    }

    [DatabaseFact]
    public async Task ASessionWithoutCertificateIsRefusedOnTheAgentsEndpoint()
    {
        await using var agent = await EnrolledAgentAsync("certificate-unbound");
        var token = await agent.AuthenticateAsync();

        agent.ThroughAgentsEndpoint = true;
        agent.ClientCertificate = TestAgent.CertificateFor(agent.Key);

        Assert.Equal(HttpStatusCode.Unauthorized, await TransferStatusAsync(agent, token));
    }

    [DatabaseFact]
    public async Task TheAgentsEndpointServesOnlyTheAgentProtocol()
    {
        using var http = factory.CreateDefaultClient(new Uri("https://localhost"));

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http, "/api/v1/auth/session"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http, "/health/live"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http, "/"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, AgentProtocol.PanelIdentityPath));
        // The same paths on the main endpoint.
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, "/health/live", agentsEndpoint: false));
    }

    [DatabaseFact]
    public async Task EnrollingThroughTheAgentsEndpointNeedsTheNewKeyAsCertificate()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "certificate-enroll");
        await using var agent = new TestAgent(factory) { ThroughAgentsEndpoint = true };

        using var someone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        agent.ClientCertificate = TestAgent.CertificateFor(someone);
        using (var refused = await agent.PostEnrollAsync(created.Enrollment.Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        agent.ClientCertificate = TestAgent.CertificateFor(agent.Key);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        Assert.NotEqual(Guid.Empty, agent.NodeId);
    }

    [DatabaseFact]
    public async Task AnAgentThatCanMoveIsToldTheAgentsAddressAndTheNodeRecordsHowItConnected()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await EnrolledAgentAsync("certificate-move", admin);
        var token = await agent.AuthenticateAsync();

        await agent.ConnectAsync(token.AccessToken, AgentCapabilities.Ping, AgentCapabilities.UseAgentsAddress);

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Commands<UseAgentsAddressCommand>(agent).Count == 1), "the address command");
        // The port alone: the agent puts it on the host it reached the panel by.
        Assert.Equal(new UseAgentsAddressCommand(AgentsEndpointApiFactory.AgentsPort), Commands<UseAgentsAddressCommand>(agent).Single());
        var node = (await NodeApi.GetAsync(admin, agent.NodeId)).Node;
        Assert.False(node.ConnectedWithCertificate);
        Assert.True(node.AgentCanUseCertificate);
        var overview = await GetTransportAsync(admin);
        Assert.Contains(overview.NodesWithoutCertificate, n => n.Id == agent.NodeId && n.Connected && n.AgentCanUseCertificate == true);

        // Moved: connected with the certificate, it is told nothing and counts as done.
        await agent.Connection!.DisposeAsync();
        token = await agent.AuthenticateWithCertificateAsync();
        await agent.ConnectAsync(token.AccessToken, AgentCapabilities.Ping, AgentCapabilities.UseAgentsAddress);
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, agent.NodeId)).Node.ConnectedWithCertificate == true, "recorded");
        Assert.Single(Commands<UseAgentsAddressCommand>(agent));
        Assert.DoesNotContain((await GetTransportAsync(admin)).NodesWithoutCertificate, n => n.Id == agent.NodeId);
    }

    [DatabaseFact]
    public async Task AnAgentThatCannotMoveIsNotToldTheAddress()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await EnrolledAgentAsync("certificate-old-agent", admin);
        var token = await agent.AuthenticateAsync();

        await agent.ConnectAsync(token.AccessToken, AgentCapabilities.Ping);

        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, agent.NodeId)).Node.AgentCanUseCertificate == false, "recorded");
        Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, agent.NodeId)).Status);
        Assert.Empty(Commands<UseAgentsAddressCommand>(agent));
    }

    [DatabaseFact]
    public async Task WithCertificatesRequiredTheMainEndpointSendsAgentsToTheirAddress()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var connected = await EnrolledAgentAsync("certificate-required-connected", admin);
        await connected.ConnectAsync((await connected.AuthenticateAsync()).AccessToken, AgentCapabilities.Ping);
        await using var agent = await EnrolledAgentAsync("certificate-required", admin);
        try
        {
            var changed = await PutTransportAsync(admin, require: true);

            Assert.True(changed.RequireClientCertificates);
            Assert.True(changed.InEffect);
            // Sessions without a certificate end: the agent connected that way is turned away.
            await connected.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            byte[] nonce = [1, 2, 3, 4];
            using var refused = await agent.PostChallengeAsync(nonce);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var error = (await refused.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, Ct))!;
            Assert.Equal("agent.certificate_required", error.Code);
            var signer = Assert.Single(agent.PinnedKeys, k => k.KeyId == error.AgentsAddress!.KeyId);
            Assert.True(ProtocolCrypto.VerifyMessage(error.AgentsAddress!, AgentProtocol.Purposes.AgentsAddress, signer.PublicKey));
            var address = ProtocolJson.Deserialize<AgentsAddress>(error.AgentsAddress!.Payload);
            Assert.Equal((agent.NodeId, AgentsEndpointApiFactory.AgentsPort, (string?)null), (address.NodeId, address.Port, address.Url));
            Assert.Equal(nonce, address.Nonce);

            // Through the agents' endpoint, with the certificate, it signs in.
            Assert.False(string.IsNullOrEmpty((await agent.AuthenticateWithCertificateAsync()).AccessToken));
        }
        finally
        {
            await PutTransportAsync(admin, require: false);
        }
    }

    [DatabaseFact]
    public async Task AHostHeaderAProxyChoseIsNeverSigned()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await EnrolledAgentAsync("certificate-host", admin);
        try
        {
            await PutTransportAsync(admin, require: true);

            using var refused = await agent.PostChallengeAsync([9], host: "evil.example");

            var error = (await refused.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, Ct))!;
            var address = ProtocolJson.Deserialize<AgentsAddress>(error.AgentsAddress!.Payload);
            Assert.Equal(AgentsEndpointApiFactory.AgentsPort, address.Port);
            Assert.Null(address.Url);
        }
        finally
        {
            await PutTransportAsync(admin, require: false);
        }
    }

    [DatabaseFact]
    public async Task ATokenAskedForAcrossTheSwitchIsRefusedToo()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await EnrolledAgentAsync("certificate-token", admin);
        var challenge = await agent.ChallengeAsync();
        try
        {
            await PutTransportAsync(admin, require: true);

            using var refused = await agent.PostTokenAsync(challenge);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var error = (await refused.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, Ct))!;
            Assert.Equal("agent.certificate_required", error.Code);
            Assert.Null(error.AgentsAddress); // the next challenge says where
        }
        finally
        {
            await PutTransportAsync(admin, require: false);
        }
    }

    [DatabaseFact]
    public async Task ANodeThatAlreadyMovedKeepsItsConnectionWhenCertificatesBecomeRequired()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await EnrolledAgentAsync("certificate-moved", admin);
        // Its session on the main endpoint outlives the move: the switch ends it, not the connection that replaced it.
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, AgentCapabilities.Ping);
        var bound = await agent.AuthenticateWithCertificateAsync();
        await agent.Connection!.DisposeAsync();
        await agent.ConnectAsync(bound.AccessToken, AgentCapabilities.Ping);
        try
        {
            await PutTransportAsync(admin, require: true);

            Assert.Equal(AckStatus.Accepted, (await NodeApi.PingAsync(admin, agent.NodeId)).Status);
        }
        finally
        {
            await PutTransportAsync(admin, require: false);
        }
    }

    [DatabaseFact]
    public async Task TheSwitchIsReadFromTheDatabaseByAPanelThatStartsAgain()
    {
        using var admin = await factory.CreateAdminClientAsync();
        try
        {
            await PutTransportAsync(admin, require: true);
            var restarted = new AgentTransportPolicy();

            await using var scope = factory.Services.CreateAsyncScope();
            await restarted.EnsureLoadedAsync(scope.ServiceProvider.GetRequiredService<Pulsar.Application.Abstractions.IPulsarDbContext>(), Ct);

            Assert.True(restarted.RequireClientCertificates);
        }
        finally
        {
            await PutTransportAsync(admin, require: false);
        }
    }

    [DatabaseFact]
    public async Task ASwitchSavedByAChangeThatThenFailsIsTheOneInEffect()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        try
        {
            // The change commits, then its call fails (a request given up as the answer came back).
            hook.AfterSave(c => c.ChangeTracker.Entries<AgentTransportSettings>().Any(), () => throw new OperationCanceledException());
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<AgentTransportManagement>()
                    .SetAsync(new UpdateAgentTransportRequest(true), AgentsUrl, Ct));
            }

            Assert.True(app.Services.GetRequiredService<AgentTransportPolicy>().RequireClientCertificates);
        }
        finally
        {
            await PutTransportAsync(admin, require: false);
        }
    }

    [DatabaseFact]
    public async Task UnknownNodesGetNoAddress()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = new TestAgent(factory);
        try
        {
            await PutTransportAsync(admin, require: true);

            using var refused = await agent.PostChallengeAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        finally
        {
            await PutTransportAsync(admin, require: false);
        }
    }

    [DatabaseFact]
    public async Task TheOverviewShowsTheAgentsAddressAndTheChangeIsAudited()
    {
        using var admin = await factory.CreateAdminClientAsync();

        var overview = await GetTransportAsync(admin);
        Assert.True(overview.EndpointConfigured);
        Assert.Equal(AgentsUrl, overview.AgentsUrl);
        Assert.False(overview.RequireClientCertificates);

        await PutTransportAsync(admin, require: true);
        await PutTransportAsync(admin, require: false);

        var audit = await admin.Http.GetStringAsync(new Uri("/api/v1/audit?action=agent_transport.changed", UriKind.Relative), Ct);
        Assert.Contains("agent_transport.changed", audit, StringComparison.Ordinal);
    }

    private async Task<TestAgent> EnrolledAgentAsync(string name, PanelClient? admin = null)
    {
        using var own = admin is null ? await factory.CreateAdminClientAsync() : null;
        var created = await NodeApi.CreateAsync(admin ?? own!, name);
        var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        return agent;
    }

    private static List<T> Commands<T>(TestAgent agent)
    {
        lock (agent.ReceivedCommands)
        {
            return [.. agent.ReceivedCommands.Select(c => c.Command).OfType<T>()];
        }
    }

    private static async Task<HttpStatusCode> TransferStatusAsync(TestAgent agent, TokenResponse token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AgentProtocol.Transfer("no-such-ticket"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await agent.Http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient http, string path, bool agentsEndpoint = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var someone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        AgentsEndpointApiFactory.AddTransportHeaders(request.Headers, agentsEndpoint, agentsEndpoint ? TestAgent.CertificateFor(someone) : null);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    private static async Task<AgentTransportOverview> GetTransportAsync(PanelClient admin) =>
        (await admin.Http.GetFromJsonAsync<AgentTransportOverview>(new Uri("/api/v1/agent-transport", UriKind.Relative), AccessScenario.Json, Ct))!;

    private static async Task<AgentTransportOverview> PutTransportAsync(PanelClient admin, bool require)
    {
        using var response = await admin.PutAsync("/api/v1/agent-transport", new UpdateAgentTransportRequest(require));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AgentTransportOverview>(AccessScenario.Json, Ct))!;
    }
}

/// <summary>Without an agents' endpoint there is nowhere to send agents: certificates cannot be required.</summary>
public sealed class AgentClientCertificateWithoutEndpointTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task CertificatesCannotBeRequiredWithoutAnAgentsEndpoint()
    {
        using var admin = await factory.CreateAdminClientAsync();

        using var response = await admin.PutAsync("/api/v1/agent-transport", new UpdateAgentTransportRequest(true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("agent_transport.no_agents_endpoint", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var overview = (await admin.Http.GetFromJsonAsync<AgentTransportOverview>(new Uri("/api/v1/agent-transport", UriKind.Relative),
            AccessScenario.Json, TestContext.Current.CancellationToken))!;
        Assert.False(overview.EndpointConfigured);
        Assert.Null(overview.AgentsUrl);
    }
}
