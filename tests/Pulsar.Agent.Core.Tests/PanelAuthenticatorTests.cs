using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// What the agent accepts from whatever answers at the panel's address before it signs anything: answers of a bounded
/// size, a nonce and a session token of the protocol's size; and as a session, only one the panel granted
/// (docs/architecture/03, "Authentication on every connection").
/// </summary>
public sealed class PanelAuthenticatorTests : IDisposable
{
    private static readonly Uri s_panel = new("https://panel.example.com/");

    private static readonly ECDsa s_panelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly PanelKey s_pinned = new(ProtocolCrypto.Fingerprint(s_panelKey.ExportSubjectPublicKeyInfo()), s_panelKey.ExportSubjectPublicKeyInfo());

    private readonly TestNodeKey _key = new();
    private readonly AgentConfig _config = new(s_panel, Guid.NewGuid(), Guid.NewGuid(),
        new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Software), [s_pinned], null, DateTimeOffset.UnixEpoch, Development: false);

    public void Dispose() => _key.Dispose();

    [Fact]
    public async Task AnAnswerLargerThanTheProtocolsIsNotRead()
    {
        // The nonce alone is 1 MiB: an agent that read it would hold it, and sign it.
        var panel = new ScriptedPanel(Challenge(RandomNumberGenerator.GetBytes(1024 * 1024)));

        await Assert.ThrowsAsync<HttpRequestException>(() => new PanelAuthenticator(panel).AuthenticateAsync(s_panel, _config, _key, null,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, _key.Signatures);
        Assert.Equal([AgentProtocol.ChallengePath], panel.Requests);
    }

    [Fact]
    public async Task ANonceLongerThanTheProtocolsIsNotSigned()
    {
        var panel = new ScriptedPanel(Challenge(RandomNumberGenerator.GetBytes(PanelAuthenticator.MaxNonceLength + 1)));

        await Assert.ThrowsAsync<PanelNotAnsweringException>(() => new PanelAuthenticator(panel).AuthenticateAsync(s_panel, _config, _key, null,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, _key.Signatures);
        Assert.Equal([AgentProtocol.ChallengePath], panel.Requests);
    }

    [Fact]
    public async Task ASessionTokenLongerThanTheProtocolsIsRefused()
    {
        var panel = new ScriptedPanel(Challenge(RandomNumberGenerator.GetBytes(32)),
            Json(new TokenResponse(new string('t', 8 * 1024), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15))));

        await Assert.ThrowsAsync<PanelNotAnsweringException>(() => new PanelAuthenticator(panel).AuthenticateAsync(s_panel, _config, _key, null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheProtocolsAnswersSignIn()
    {
        // The panel's clock is an hour ahead of this machine's: the grant says so.
        var panel = new GrantingPanel(_config, TimeSpan.FromHours(1));

        var signedIn = await new PanelAuthenticator(panel, TimeProvider.System).SignInAsync(s_panel, _config, _key, null, TestContext.Current.CancellationToken);

        Assert.Equal("session-token", signedIn.Token.AccessToken);
        Assert.Equal(signedIn.Token.SessionId, signedIn.Grant.SessionId);
        Assert.InRange(signedIn.PanelClockOffset, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        Assert.Equal(1, _key.Signatures);
    }

    [Fact]
    public async Task ASessionWithoutThePanelsGrantIsNotTaken()
    {
        var panel = new ScriptedPanel(Challenge(RandomNumberGenerator.GetBytes(32)),
            Json(new TokenResponse("session-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15))));

        await Assert.ThrowsAsync<PanelNotAnsweringException>(() => new PanelAuthenticator(panel).AuthenticateAsync(s_panel, _config, _key, null,
            TestContext.Current.CancellationToken));
    }

    private static HttpResponseMessage Challenge(byte[] nonce) => Json(new ChallengeResponse(Guid.NewGuid(), nonce, PanelAddress.Origin(s_panel)));

    private static HttpResponseMessage Json<T>(T value)
    {
        var content = new ByteArrayContent(ProtocolJson.Serialize(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Signs in as the panel does, its clock <paramref name="clockOffset"/> ahead of this machine's.</summary>
    private sealed class GrantingPanel(AgentConfig config, TimeSpan clockOffset) : PanelTransport
    {
        private readonly AgentConfig _config = config;
        private readonly TimeSpan _clockOffset = clockOffset;
        private byte[]? _nonce;

        public override HttpMessageHandler CreateHttpHandler() => new Handler(this);

        public override HttpMessageHandler WithClientCertificate(HttpMessageHandler handler, X509Certificate2 certificate) => handler;

        private sealed class Handler(GrantingPanel panel) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath == AgentProtocol.ChallengePath)
                {
                    panel._nonce = (await request.Content!.ReadFromJsonAsync<ChallengeRequest>(ProtocolJson.Options, cancellationToken))!.Nonce;
                    return Challenge(RandomNumberGenerator.GetBytes(32));
                }

                var now = DateTimeOffset.UtcNow + panel._clockOffset;
                var session = Guid.NewGuid();
                var grant = new SessionGrant(panel._config.NodeId, panel._config.CredentialId, session, now.AddMinutes(15), panel._nonce, now);
                return Json(new TokenResponse("session-token", session, now.AddMinutes(15),
                    ProtocolCrypto.SignMessage(s_panelKey, s_pinned.KeyId, AgentProtocol.Purposes.SessionGrant, ProtocolJson.Serialize(grant))));
            }
        }
    }

    /// <summary>Answers each request with the next answer, in order.</summary>
    private sealed class ScriptedPanel(params HttpResponseMessage[] answers) : PanelTransport
    {
        private readonly Queue<HttpResponseMessage> _answers = new(answers);

        public List<string> Requests { get; } = [];

        public override HttpMessageHandler CreateHttpHandler() => new Handler(this);

        public override HttpMessageHandler WithClientCertificate(HttpMessageHandler handler, X509Certificate2 certificate) => handler;

        private sealed class Handler(ScriptedPanel panel) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                panel.Requests.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(panel._answers.Dequeue());
            }
        }
    }

    private sealed class TestNodeKey : INodeKey
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private int _signatures;

        public int Signatures => _signatures;

        public NodeKeyReference Reference { get; } = new("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Software);

        public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> data)
        {
            Interlocked.Increment(ref _signatures);
            return ProtocolCrypto.Sign(_key, data);
        }

        public X509Certificate2 CreateCertificate() => NodeCertificate.Create(_key);

        public void Dispose() => _key.Dispose();
    }
}
