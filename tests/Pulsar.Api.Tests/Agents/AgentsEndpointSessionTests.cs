using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Agents;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// What signs a connection of the agents' endpoint in (real Kestrel and TLS): only a valid agent session presented with
/// the key of the connection's certificate, and a node holds so many such connections, which go with its session.
/// </summary>
public sealed class AgentsEndpointSessionTests(AgentsEndpointSessionTests.Panel factory) : IClassFixture<AgentsEndpointSessionTests.Panel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AgentsConnectionLimit Limit => factory.Services.GetRequiredService<AgentsEndpoint>().ConnectionLimit;

    [DatabaseFact]
    public async Task OnlyAValidSessionWithItsKeySignsAConnectionIn()
    {
        factory.StartServer();
        await QuietAsync();
        using var a = await SignInAsync("session-a");
        using var main = await SignInAsync("session-main", throughMain: true);
        using var ended = await SignInAsync("session-ended");
        factory.Services.GetRequiredService<AgentSessionStore>().EndAll(new NodeId(ended.NodeId));
        await QuietAsync();

        using var strangerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var strangerCertificate = KestrelPanelFactory.UsableCertificate(strangerKey);
        using var stranger = Holder(strangerCertificate);

        // A random token, another node's session without its key, a session of the main endpoint: refused, still waiting.
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferAsync(stranger, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Assert.Equal(1, Limit.Pending);
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferAsync(stranger, a.Token.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferAsync(stranger, main.Token.AccessToken));
        Assert.Equal(1, Limit.Pending);

        // A session that ended, with its own key: still waiting too.
        using var late = Holder(ended.Certificate);
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferAsync(late, ended.Token.AccessToken));
        Assert.Equal(2, Limit.Pending);
        Assert.Equal(0, Limit.SignedIn(new NodeId(ended.NodeId)));

        // The session with its key: signed in.
        using var legit = Holder(a.Certificate);
        Assert.Equal(HttpStatusCode.NotFound, await TransferAsync(legit, a.Token.AccessToken));
        Assert.Equal(2, Limit.Pending);
        Assert.Equal(1, Limit.SignedIn(new NodeId(a.NodeId)));
    }

    [DatabaseFact]
    public async Task ANodeHoldsSoManyConnectionsAndLosesThemWhenItsKeyIsRevoked()
    {
        factory.StartServer();
        await QuietAsync();
        using var mallory = await SignInAsync("session-mallory");
        var node = new NodeId(mallory.NodeId);
        await QuietAsync();

        // One session, one key, many connections: two sign in, the third is refused and waits like a stranger's.
        using var first = Holder(mallory.Certificate);
        using var second = Holder(mallory.Certificate);
        using var third = Holder(mallory.Certificate);
        Assert.Equal(HttpStatusCode.NotFound, await TransferAsync(first, mallory.Token.AccessToken));
        Assert.Equal(HttpStatusCode.NotFound, await TransferAsync(second, mallory.Token.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await TransferAsync(third, mallory.Token.AccessToken));
        Assert.Equal(2, Limit.SignedIn(node));
        Assert.Equal(1, Limit.Pending);

        // Another agent still gets in.
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherCertificate = KestrelPanelFactory.UsableCertificate(otherKey);
        using (var other = Holder(otherCertificate))
        {
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct)).StatusCode);
        }

        // Revoked: its sessions end and so do their connections, at once; requests without a session keep none.
        await factory.RevokeCredentialAsync(mallory.NodeId, mallory.CredentialId);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Limit.SignedIn(node) == 0), "the node's connections closed");
        using var response = await first.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, Limit.SignedIn(node));
    }

    [DatabaseFact]
    public async Task ARotationClosesTheOldKeysConnectionsWithoutAHubConnection()
    {
        // Whoever holds the old key signed in before the rotation (no hub connection of theirs registered): its signed-in
        // connections go with the rotation, or they would hold the node's share of the endpoint for up to 15 minutes.
        factory.StartServer();
        await QuietAsync();
        using var old = await SignInAsync("rotation-closes-old");
        var node = new NodeId(old.NodeId);
        await QuietAsync();
        using var thief = Holder(old.Certificate);
        Assert.Equal(HttpStatusCode.NotFound, await TransferAsync(thief, old.Token.AccessToken));
        Assert.Equal(1, Limit.SignedIn(node));

        // The rotation, as the agent makes it: registered within the old key's session, completed by the new key's sign-in.
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newCertificate = KestrelPanelFactory.UsableCertificate(newKey);
        var newPublicKey = newKey.ExportSubjectPublicKeyInfo();
        byte[] RotationData(string purpose) => ProtocolCrypto.SignedData(purpose, ProtocolCrypto.GuidBytes(old.NodeId), ProtocolCrypto.GuidBytes(old.CredentialId),
            ProtocolCrypto.GuidBytes(old.Token.SessionId), newPublicKey);
        Guid newCredential;
        using (var agents = new HttpClient(factory.Handler(old.Certificate)) { BaseAddress = factory.AgentsUrl })
        using (var request = new HttpRequestMessage(HttpMethod.Post, AgentProtocol.RotateCredentialPath.TrimStart('/')))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", old.Token.AccessToken);
            request.Content = JsonContent.Create(new RotateCredentialRequest(newPublicKey, "software", ProtocolCrypto.Sign(newKey,
                RotationData(AgentProtocol.Purposes.RotateCredential)), ProtocolCrypto.Sign(old.Key, RotationData(AgentProtocol.Purposes.AuthorizeRotation))),
                options: ProtocolJson.Options);
            using var rotated = await agents.SendAsync(request, Ct);
            rotated.EnsureSuccessStatusCode();
            newCredential = ProtocolJson.Deserialize<CredentialRotation>((await rotated.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!.Payload)
                .CredentialId;
        }

        var token = await SignInWithAsync(old.NodeId, newCredential, newKey, newCertificate);

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Limit.SignedIn(node) == 0), () => $"{Limit.SignedIn(node)} signed-in connection(s) left");
        using var current = Holder(newCertificate);
        Assert.Equal(HttpStatusCode.NotFound, await TransferAsync(current, token.AccessToken));
        Assert.Equal(1, Limit.SignedIn(node));
    }

    /// <summary>Waits until no connection of an earlier step is left.</summary>
    private Task QuietAsync() => InProcessAgent.EventuallyAsync(() => Task.FromResult(Limit.Open == 0),
        () => $"no connection left (open {Limit.Open}, pending {Limit.Pending})");

    /// <summary>A client that keeps its one connection for as long as it lives.</summary>
    private HttpClient Holder(X509Certificate2 certificate)
    {
        var handler = factory.Handler(certificate);
        handler.PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan;
        handler.PooledConnectionLifetime = Timeout.InfiniteTimeSpan;
        handler.MaxConnectionsPerServer = 1;
        return new HttpClient(handler) { BaseAddress = factory.AgentsUrl, Timeout = TimeSpan.FromSeconds(30) };
    }

    private static async Task<HttpStatusCode> TransferAsync(HttpClient http, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AgentProtocol.Transfer("no-such-ticket").TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>A node enrolled through the agents' endpoint, signed in there (or on the main endpoint, without a certificate).</summary>
    private async Task<SignedIn> SignInAsync(string name, bool throughMain = false)
    {
        var created = await factory.CreateNodeAsync(name);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificate = KestrelPanelFactory.UsableCertificate(key);
        using var agents = new HttpClient(factory.Handler(certificate)) { BaseAddress = factory.AgentsUrl };
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var proof = ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(AgentProtocol.Purposes.Enroll, ProtocolCrypto.Utf8(created.Enrollment.Token), publicKey));
        using var enrolled = await agents.PostAsJsonAsync(AgentProtocol.EnrollPath.TrimStart('/'),
            new EnrollRequest(created.Enrollment.Token, publicKey, "software", "0.1.0-test", TestAgent.Inventory, proof), ProtocolJson.Options, Ct);
        enrolled.EnsureSuccessStatusCode();
        var result = ProtocolJson.Deserialize<EnrollmentResult>((await enrolled.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!.Payload);

        using var http = throughMain ? new HttpClient(factory.Handler()) { BaseAddress = factory.MainUrl } : agents;
        using var challengeResponse = await http.PostAsJsonAsync(AgentProtocol.ChallengePath.TrimStart('/'),
            new ChallengeRequest(result.NodeId, result.CredentialId), ProtocolJson.Options, Ct);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!;
        var signature = ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(result.NodeId), ProtocolCrypto.GuidBytes(result.CredentialId), challenge.Nonce, ProtocolCrypto.Utf8(challenge.PanelOrigin)));
        using var tokenResponse = await http.PostAsJsonAsync(AgentProtocol.TokenPath.TrimStart('/'),
            new TokenRequest(challenge.ChallengeId, signature), ProtocolJson.Options, Ct);
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(ProtocolJson.Options, Ct))!;
        return new SignedIn(result.NodeId, result.CredentialId, key, certificate, token);
    }

    /// <summary>Signs a credential in through the agents' endpoint, its key presented as client certificate.</summary>
    private async Task<TokenResponse> SignInWithAsync(Guid nodeId, Guid credentialId, ECDsa key, X509Certificate2 certificate)
    {
        using var http = new HttpClient(factory.Handler(certificate)) { BaseAddress = factory.AgentsUrl };
        using var challengeResponse = await http.PostAsJsonAsync(AgentProtocol.ChallengePath.TrimStart('/'),
            new ChallengeRequest(nodeId, credentialId, RandomNumberGenerator.GetBytes(32)), ProtocolJson.Options, Ct);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!;
        var signature = ProtocolCrypto.Sign(key, ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(nodeId), ProtocolCrypto.GuidBytes(credentialId), challenge.Nonce, ProtocolCrypto.Utf8(challenge.PanelOrigin)));
        using var tokenResponse = await http.PostAsJsonAsync(AgentProtocol.TokenPath.TrimStart('/'),
            new TokenRequest(challenge.ChallengeId, signature), ProtocolJson.Options, Ct);
        tokenResponse.EnsureSuccessStatusCode();
        return (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(ProtocolJson.Options, Ct))!;
    }

    private sealed record SignedIn(Guid NodeId, Guid CredentialId, ECDsa Key, X509Certificate2 Certificate, TokenResponse Token) : IDisposable
    {
        public void Dispose()
        {
            Certificate.Dispose();
            Key.Dispose();
        }
    }

    public sealed class Panel : KestrelPanelFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Agents:MaxConnections", "8");
            builder.UseSetting("Agents:MaxPendingConnections", "8");
            builder.UseSetting("Agents:MaxConnectionsPerNode", "2");
            builder.ConfigureTestServices(services => services.Configure<AgentsOptions>(o => o.SignInDeadline = TimeSpan.FromSeconds(20)));
        }
    }
}
