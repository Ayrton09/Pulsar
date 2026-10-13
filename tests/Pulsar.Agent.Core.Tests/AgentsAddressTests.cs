using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// The agents' address (Phase 16, docs/architecture/03 "Client certificates"): which refusals the agent follows, where
/// they send it, the certificate it presents there, and which failures send it to its fallback.
/// </summary>
public sealed class AgentsAddressTests : IDisposable
{
    private static readonly Uri s_main = new("https://panel.example.com:8443/");
    private readonly ECDsa _panelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly AgentConfig _config;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);

    public AgentsAddressTests()
    {
        var publicKey = _panelKey.ExportSubjectPublicKeyInfo();
        _config = new AgentConfig(s_main, Guid.CreateVersion7(), Guid.CreateVersion7(), new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Software),
            [new PanelKey(ProtocolCrypto.Fingerprint(publicKey), publicKey)], null, DateTimeOffset.UnixEpoch, Development: false);
    }

    public void Dispose()
    {
        _panelKey.Dispose();
        _otherKey.Dispose();
    }

    [Fact]
    public void ARefusalSignedForThisNodeAndRequestSendsItToThePortOnTheHostItReached()
    {
        var target = AgentsAddressHint.Verify(Sign(new AgentsAddress(_config.NodeId, 8444, null, _nonce)), _config, s_main, _nonce);

        Assert.Equal(new Uri("https://panel.example.com:8444/"), target);
    }

    [Fact]
    public void TheOperatorsAddressIsTakenAsItIs()
    {
        var target = AgentsAddressHint.Verify(Sign(new AgentsAddress(_config.NodeId, 8444, "https://Agents.Example.com:9444", _nonce)), _config, s_main, _nonce);

        Assert.Equal(new Uri("https://agents.example.com:9444/"), target);
    }

    [Fact]
    public void RefusalsThatDoNotAnswerThisRequestOfThisNodeAreNotFollowed()
    {
        var payload = new AgentsAddress(_config.NodeId, 8444, null, _nonce);

        Assert.Null(AgentsAddressHint.Verify(null, _config, s_main, _nonce));
        Assert.Null(AgentsAddressHint.Verify(Sign(payload, _otherKey), _config, s_main, _nonce)); // a key it does not pin
        Assert.Null(AgentsAddressHint.Verify(Sign(payload, purpose: AgentProtocol.Purposes.RotationResult), _config, s_main, _nonce));
        Assert.Null(AgentsAddressHint.Verify(Sign(payload with { NodeId = Guid.CreateVersion7() }), _config, s_main, _nonce));
        Assert.Null(AgentsAddressHint.Verify(Sign(payload with { Nonce = RandomNumberGenerator.GetBytes(32) }), _config, s_main, _nonce)); // played back
        Assert.Null(AgentsAddressHint.Verify(Sign(payload with { Nonce = null }), _config, s_main, _nonce));
        Assert.Null(AgentsAddressHint.Verify(Sign(payload), _config, s_main, null));
        Assert.Null(AgentsAddressHint.Verify(Sign(payload with { Port = 0 }), _config, s_main, _nonce));
        Assert.Null(AgentsAddressHint.Verify(Sign(payload with { Url = "http://agents.example.com:9444" }), _config, s_main, _nonce));
        Assert.Null(AgentsAddressHint.Verify(Sign(payload with { Port = 8443 }), _config, s_main, _nonce)); // where it was refused
        var garbled = Sign(payload) with { Payload = "not json"u8.ToArray() };
        Assert.Null(AgentsAddressHint.Verify(garbled with { Signature = Sign(garbled.Payload) }, _config, s_main, _nonce));
    }

    [Theory]
    [InlineData("https://panel.example.com/", 8444, null, "https://panel.example.com:8444/")]
    [InlineData("https://[::1]:8443/", 8444, null, "https://[::1]:8444/")]
    [InlineData("http://localhost:5290/", 8444, null, "https://localhost:8444/")]
    [InlineData("https://panel.example.com/", 0, "https://agents.example.com", "https://agents.example.com/")]
    public void TheAgentsAddressIsThePortOnTheHostReachedOrTheOperatorsAddress(string reached, int port, string? url, string expected)
    {
        Assert.Equal(new Uri(expected), PanelAddress.AgentsAddress(new Uri(reached), port, url));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(65536, null)]
    [InlineData(8444, "http://agents.example.com")]
    [InlineData(8444, "https://agents.example.com/path")]
    public void AnAgentsAddressWithoutAPortOrHttpsOriginIsNone(int port, string? url)
    {
        Assert.Null(PanelAddress.AgentsAddress(s_main, port, url));
    }

    [Fact]
    public void TheCertificateIsTheNodeKeyForClientAuthentication()
    {
        using var certificate = NodeCertificate.Create(_otherKey);

        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(_otherKey.ExportSubjectPublicKeyInfo(), certificate.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
        Assert.Contains(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, certificate.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
        Assert.True(certificate.NotBefore < DateTime.Now && certificate.NotAfter > DateTime.Now.AddYears(9));
    }

    [Fact]
    public void OnlyFailuresWhereNoPanelAnsweredSendTheAgentToItsFallback()
    {
        var running = CancellationToken.None;
        Assert.True(AgentConnection.IsUnreachable(new HttpRequestException("refused"), running));
        Assert.True(AgentConnection.IsUnreachable(new System.Security.Authentication.AuthenticationException("TLS"), running));
        Assert.True(AgentConnection.IsUnreachable(new System.Net.WebSockets.WebSocketException("closed"), running));
        Assert.True(AgentConnection.IsUnreachable(new PanelNotAnsweringException("502"), running));
        Assert.True(AgentConnection.IsUnreachable(new TaskCanceledException("timeout"), running));
        Assert.True(AgentConnection.IsUnreachable(new TimeoutException("no Hello"), running));

        // The panel answered: the fallback would only say it again.
        Assert.False(AgentConnection.IsUnreachable(new AgentRejectedException("revoked"), running));
        Assert.False(AgentConnection.IsUnreachable(new CertificateRequiredException("required"), running));
        Assert.False(AgentConnection.IsUnreachable(new AgentException("refused the challenge"), running));
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        Assert.False(AgentConnection.IsUnreachable(new TaskCanceledException("stopping"), stopping.Token));
    }

    [Fact]
    public async Task APanelThatRequiresCertificatesIsToldApartFromOtherAnswers()
    {
        var signed = Sign(new AgentsAddress(_config.NodeId, 8444, null, _nonce));

        var required = await Assert.ThrowsAsync<CertificateRequiredException>(() =>
            PostAsync(HttpStatusCode.Forbidden, new AgentError(CertificateRequiredException.Code, "Use the agents' address.", signed)));
        Assert.Equal(signed.Signature, required.AgentsAddress!.Signature);

        var forbidden = await Assert.ThrowsAsync<AgentException>(() => PostAsync(HttpStatusCode.Forbidden, new AgentError("agent.other", "No.")));
        Assert.IsNotType<CertificateRequiredException>(forbidden);
        await Assert.ThrowsAsync<PanelNotAnsweringException>(() => PostAsync(HttpStatusCode.NotFound, null));
        await Assert.ThrowsAsync<PanelNotAnsweringException>(() => PostAsync(HttpStatusCode.BadGateway, null));
    }

    [Fact]
    public async Task OnlyThePanelsOwnRefusalIsARefusal()
    {
        // The panel says why in the protocol's words: the agent stops trying there, and never takes it elsewhere.
        var refused = await Assert.ThrowsAsync<AgentRejectedException>(() =>
            PostAsync(HttpStatusCode.Unauthorized, new AgentError("agent.authentication_rejected", "The node cannot authenticate.")));
        Assert.False(AgentConnection.IsUnreachable(refused, CancellationToken.None));

        // Another service on that port (the agents' port given to something else): its refusals and pages send the agent
        // to its fallback address like no answer at all.
        foreach (var other in new Func<Task>[]
        {
            () => PostAsync(HttpStatusCode.Unauthorized, null),
            () => PostAsync(HttpStatusCode.Forbidden, null),
            () => PostTextAsync(HttpStatusCode.Unauthorized, "<html>Sign in</html>"),
            () => PostTextAsync(HttpStatusCode.OK, "<html>Welcome</html>"),
            () => PostTextAsync(HttpStatusCode.OK, "null"),
            () => PostTextAsync(HttpStatusCode.OK, "{\"some\": \"thing\"} trailing"),
        })
        {
            var notPanel = await Assert.ThrowsAsync<PanelNotAnsweringException>(other);
            Assert.True(AgentConnection.IsUnreachable(notPanel, CancellationToken.None));
        }
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        // A 307 or 308 would send the enrollment, the challenge's answer or a new key's proof on to wherever it says.
        using var handler = new PanelTransport().CreateHttpHandler();
        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);

        await Assert.ThrowsAsync<PanelNotAnsweringException>(() => PostAsync(HttpStatusCode.TemporaryRedirect, null));
        await Assert.ThrowsAsync<PanelNotAnsweringException>(() => PostAsync(HttpStatusCode.PermanentRedirect, null));
    }

    private static async Task PostAsync(HttpStatusCode status, AgentError? error)
    {
        using var http = new HttpClient(new Answering(status, error)) { BaseAddress = s_main };
        await PanelTransport.PostAsync<ChallengeRequest, ChallengeResponse>(http, AgentProtocol.ChallengePath,
            new ChallengeRequest(Guid.Empty, Guid.Empty), TestContext.Current.CancellationToken);
    }

    private static async Task PostTextAsync(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new AnsweringText(status, body)) { BaseAddress = s_main };
        await PanelTransport.PostAsync<ChallengeRequest, ChallengeResponse>(http, AgentProtocol.ChallengePath,
            new ChallengeRequest(Guid.Empty, Guid.Empty), TestContext.Current.CancellationToken);
    }

    private sealed class AnsweringText(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, body.StartsWith('<') ? "text/html" : "application/json"),
            });
    }

    private SignedMessage Sign(AgentsAddress address, ECDsa? key = null, string purpose = AgentProtocol.Purposes.AgentsAddress)
    {
        var signer = key ?? _panelKey;
        var payload = ProtocolJson.Serialize(address);
        return ProtocolCrypto.SignMessage(signer, ProtocolCrypto.Fingerprint(signer.ExportSubjectPublicKeyInfo()), purpose, payload);
    }

    private byte[] Sign(byte[] payload) => ProtocolCrypto.Sign(_panelKey, ProtocolCrypto.SignedData(AgentProtocol.Purposes.AgentsAddress, payload));

    private sealed class Answering(HttpStatusCode status, AgentError? error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = error is null ? null : JsonContent.Create(error, options: ProtocolJson.Options) });
    }
}
