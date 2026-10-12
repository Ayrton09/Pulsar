using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Agent.Core.Panel;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// The agents' endpoint (Phase 16) on real Kestrel with real TLS: the endpoint from configuration asks for the client
/// certificate in the handshake, and the panel binds the agent's session to the key it presented.
/// </summary>
public sealed class AgentsEndpointKestrelTests(KestrelPanelFactory factory) : IClassFixture<KestrelPanelFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AnAgentEnrollsAndSignsInWithItsKeyInTheTlsHandshake()
    {
        factory.StartServer();
        var created = await factory.CreateNodeAsync("kestrel-agent");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = KestrelPanelFactory.UsableCertificate(key);
        using var agents = new HttpClient(factory.Handler(certificate)) { BaseAddress = factory.AgentsUrl };
        using var main = new HttpClient(factory.Handler()) { BaseAddress = factory.MainUrl };

        // Enrollment through the agents' endpoint, the key in the handshake.
        var identity = (await agents.GetFromJsonAsync<PanelIdentityResponse>(AgentProtocol.PanelIdentityPath.TrimStart('/'), ProtocolJson.Options, Ct))!;
        var panelKey = Assert.Single(identity.Keys, k => k.KeyId == created.Enrollment.PanelKeyFingerprint);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var proof = ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(AgentProtocol.Purposes.Enroll, ProtocolCrypto.Utf8(created.Enrollment.Token), publicKey));
        using var enrolled = await agents.PostAsJsonAsync(AgentProtocol.EnrollPath.TrimStart('/'),
            new EnrollRequest(created.Enrollment.Token, publicKey, "software", "0.1.0-test", TestAgent.Inventory, proof), ProtocolJson.Options, Ct);
        enrolled.EnsureSuccessStatusCode();
        var signed = (await enrolled.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!;
        Assert.True(ProtocolCrypto.VerifyMessage(signed, AgentProtocol.Purposes.EnrollmentResult, panelKey.PublicKey));
        var result = ProtocolJson.Deserialize<EnrollmentResult>(signed.Payload);

        // The challenge binds the agents' address, port included.
        using var challengeResponse = await agents.PostAsJsonAsync(AgentProtocol.ChallengePath.TrimStart('/'),
            new ChallengeRequest(result.NodeId, result.CredentialId), ProtocolJson.Options, Ct);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!;
        Assert.Equal($"https://127.0.0.1:{factory.AgentsPort}", challenge.PanelOrigin);
        var signature = ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(result.NodeId), ProtocolCrypto.GuidBytes(result.CredentialId), challenge.Nonce, ProtocolCrypto.Utf8(challenge.PanelOrigin)));
        using var tokenResponse = await agents.PostAsJsonAsync(AgentProtocol.TokenPath.TrimStart('/'),
            new TokenRequest(challenge.ChallengeId, signature), ProtocolJson.Options, Ct);
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(ProtocolJson.Options, Ct))!;

        // The session works with the certificate, not with the token alone on the main endpoint.
        Assert.Equal(HttpStatusCode.NotFound, await TransferStatusAsync(agents, token));
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferStatusAsync(main, token));
    }

    [DatabaseFact]
    public async Task TheAgentsEndpointNeedsACertificateAndServesOnlyTheAgentProtocol()
    {
        factory.StartServer();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = KestrelPanelFactory.UsableCertificate(key);
        using var agents = new HttpClient(factory.Handler(certificate)) { BaseAddress = factory.AgentsUrl };
        using var bare = new HttpClient(factory.Handler()) { BaseAddress = factory.AgentsUrl };
        using var main = new HttpClient(factory.Handler()) { BaseAddress = factory.MainUrl };

        await Assert.ThrowsAsync<HttpRequestException>(() => bare.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct));

        Assert.Equal(HttpStatusCode.NotFound, (await agents.GetAsync("api/v1/auth/session", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agents.GetAsync("health/live", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agents.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct)).StatusCode);
        // Browsers use the main endpoint, which asks for no certificate.
        Assert.Equal(HttpStatusCode.OK, (await main.GetAsync("api/v1/auth/session", Ct)).StatusCode);
    }

    [DatabaseFact]
    public async Task TheRealAgentMovesToTheAgentsAddressOverRealTls()
    {
        factory.StartServer();
        var created = await factory.CreateNodeAsync("kestrel-move");
        await using var agent = new InProcessAgent(factory, transport: new TrustingTransport(factory));
        var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint, factory.MainUrl.ToString());

        await agent.StartAsync();

        // Told the address of the agents' endpoint (the host it reached the panel by, the endpoint's port), it signs in
        // there with its node key in the TLS handshake and opens the hub over WebSockets with it.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == factory.AgentsUrl), "the agent on the agents' address");
        Assert.Equal(factory.MainUrl, agent.LiveConfig!.FallbackPanel);
        var connections = factory.Services.GetRequiredService<IAgentConnections>();
        var ping = await connections.SendAsync(new NodeId(config.NodeId), new PingCommand("over mTLS"), null, Ct);
        Assert.Equal(AckStatus.Accepted, ping?.Status);
        await using var scope = factory.Services.CreateAsyncScope();
        var node = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Nodes.AsNoTracking()
            .SingleAsync(n => n.Id == new NodeId(config.NodeId), Ct);
        Assert.True(node.ConnectedWithCertificate);
    }

    private static async Task<HttpStatusCode> TransferStatusAsync(HttpClient http, TokenResponse token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AgentProtocol.Transfer("no-such-ticket").TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>The agent's real transport (sockets, TLS, WebSockets), trusting the test panel's certificate.</summary>
    private sealed class TrustingTransport(KestrelPanelFactory panel) : PanelTransport
    {
        public override HttpMessageHandler CreateHttpHandler() => panel.Handler();

        public override void ConfigureHub(HttpConnectionOptions options) =>
            options.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, certificate, _, _) => panel.IsPanel(certificate);
    }
}
