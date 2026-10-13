using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddJsonProtocol
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// A minimal agent speaking the real protocol against the panel under test: pins the panel key, enrolls with a fresh
/// P-256 key, authenticates by challenge and connects to the hub over WebSockets. Received commands are checked
/// against the pinned key before they are accepted.
/// </summary>
public sealed class TestAgent : IAsyncDisposable
{
    private static readonly Uri s_baseAddress = new("https://localhost");
    private readonly WebApplicationFactory<Program> _factory;

    public TestAgent(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        Http = factory.CreateDefaultClient(s_baseAddress, new TransportHeaders(this));
    }

    /// <summary>
    /// Requests go through the panel's agents' endpoint (Phase 16), as <see cref="AgentsEndpointApiFactory"/> simulates
    /// it: TestServer has no TLS, so the endpoint and the client certificate travel as test headers.
    /// </summary>
    public bool ThroughAgentsEndpoint { get; set; }

    /// <summary>The TLS client certificate the requests present (only the agents' endpoint asks for one).</summary>
    public X509Certificate2? ClientCertificate { get; set; }

    /// <summary>A self-signed certificate on <paramref name="key"/>, as the agent makes one on its node key.</summary>
    public static X509Certificate2 CertificateFor(ECDsa key) =>
        new CertificateRequest("CN=Pulsar node", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

    /// <summary>Signs in through the agents' endpoint with a certificate on the node key.</summary>
    public Task<TokenResponse> AuthenticateWithCertificateAsync()
    {
        ThroughAgentsEndpoint = true;
        ClientCertificate = CertificateFor(Key);
        // TestServer's requests all say Host: localhost, whichever endpoint they simulate.
        return AuthenticateAsync();
    }

    public HttpClient Http { get; }

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Guid NodeId { get; private set; }

    public Guid CredentialId { get; private set; }

    public IReadOnlyList<PanelKey> PinnedKeys { get; private set; } = [];

    public HubConnection? Connection { get; private set; }

    public List<CommandEnvelope> ReceivedCommands { get; } = [];

    public TaskCompletionSource ClosedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static NodeInventory Inventory { get; } = new("TEST-NODE", "Windows 11 Pro", "26200", "Test CPU", 8, 16L * 1024 * 1024 * 1024, @"D:\PulsarServers");

    /// <summary>The machine name this agent reports (reusable tokens name their nodes after it).</summary>
    public string Hostname { get; init; } = Inventory.Hostname;

    /// <summary>The panel said the node waits for approval.</summary>
    public bool AwaitingApproval { get; private set; }

    /// <summary>Enrolls and returns the raw response (for rejection tests). <paramref name="proofKey"/> forges the proof.</summary>
    public Task<HttpResponseMessage> PostEnrollAsync(string token, ECDsa? proofKey = null)
    {
        var publicKey = Key.ExportSubjectPublicKeyInfo();
        var proof = ProtocolCrypto.Sign(proofKey ?? Key,
            ProtocolCrypto.SignedData(AgentProtocol.Purposes.Enroll, ProtocolCrypto.Utf8(token), publicKey));
        return Http.PostAsJsonAsync(AgentProtocol.EnrollPath,
            new EnrollRequest(token, publicKey, "software", "0.1.0-test", Inventory with { Hostname = Hostname }, proof), ProtocolJson.Options, Ct);
    }

    /// <summary>The full enrollment, as the real agent does it: pin the panel key first, then verify the answer with it.</summary>
    public async Task EnrollAsync(string token, string expectedPanelKey)
    {
        var identity = await Http.GetFromJsonAsync<PanelIdentityResponse>(AgentProtocol.PanelIdentityPath, ProtocolJson.Options, Ct);
        var pinned = Assert.Single(identity!.Keys, k => k.KeyId == expectedPanelKey);
        Assert.Equal(expectedPanelKey, ProtocolCrypto.Fingerprint(pinned.PublicKey));

        using var response = await PostEnrollAsync(token);
        response.EnsureSuccessStatusCode();
        var signed = (await response.Content.ReadFromJsonAsync<SignedMessage>(ProtocolJson.Options, Ct))!;
        Assert.True(ProtocolCrypto.VerifyMessage(signed, AgentProtocol.Purposes.EnrollmentResult, pinned.PublicKey));

        var result = ProtocolJson.Deserialize<EnrollmentResult>(signed.Payload);
        NodeId = result.NodeId;
        CredentialId = result.CredentialId;
        PinnedKeys = result.PanelKeys;
        AwaitingApproval = result.AwaitingApproval;
    }

    /// <summary>Signs in with another credential of the node from now on (one a rotation registered); pass its key to <see cref="PostTokenAsync"/>.</summary>
    public void UseCredential(Guid credentialId) => CredentialId = credentialId;

    /// <param name="nonce">Sent with the request, signed back in a refusal that names the agents' address.</param>
    /// <param name="host">The Host header, as a proxy in front of the panel could set it.</param>
    public async Task<HttpResponseMessage> PostChallengeAsync(byte[]? nonce = null, string? host = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentProtocol.ChallengePath)
        {
            Content = JsonContent.Create(new ChallengeRequest(NodeId, CredentialId, nonce), options: ProtocolJson.Options),
        };
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return await Http.SendAsync(request, Ct);
    }

    public async Task<ChallengeResponse> ChallengeAsync()
    {
        using var response = await PostChallengeAsync();
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChallengeResponse>(ProtocolJson.Options, Ct))!;
    }

    public Task<HttpResponseMessage> PostTokenAsync(ChallengeResponse challenge, ECDsa? signer = null)
    {
        var signature = ProtocolCrypto.Sign(signer ?? Key, ProtocolCrypto.SignedData(AgentProtocol.Purposes.Authenticate,
            ProtocolCrypto.GuidBytes(NodeId), ProtocolCrypto.GuidBytes(CredentialId), challenge.Nonce, ProtocolCrypto.Utf8(challenge.PanelOrigin)));
        return Http.PostAsJsonAsync(AgentProtocol.TokenPath, new TokenRequest(challenge.ChallengeId, signature), ProtocolJson.Options, Ct);
    }

    public async Task<TokenResponse> AuthenticateAsync(string expectedOrigin = "https://localhost")
    {
        var challenge = await ChallengeAsync();
        Assert.Equal(expectedOrigin, challenge.PanelOrigin);
        using var response = await PostTokenAsync(challenge);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(ProtocolJson.Options, Ct))!;
    }

    /// <summary>Connects to the hub with the session token and says Hello.</summary>
    public async Task<HelloAccepted> ConnectAsync(string accessToken, params string[] capabilities)
    {
        var server = _factory.Server;
        Connection = new HubConnectionBuilder()
            .WithUrl(new Uri("wss://localhost" + AgentProtocol.HubPath), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        request.Headers.Authorization = $"Bearer {accessToken}";
                        AgentsEndpointApiFactory.AddTransportHeaders(request.Headers, ThroughAgentsEndpoint, ClientCertificate);
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(options => ProtocolJson.Configure(options.PayloadSerializerOptions))
            .Build();

        Connection.On<SignedMessage, CommandAck>(nameof(IAgentClient.Execute), Execute);
        Connection.Closed += _ =>
        {
            ClosedSignal.TrySetResult();
            return Task.CompletedTask;
        };

        await Connection.StartAsync(Ct);
        return await Connection.InvokeAsync<HelloAccepted>(nameof(IAgentHub.Hello),
            new AgentHello("0.1.0-test", AgentProtocol.CurrentVersion, capabilities, Inventory), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
        }

        Http.Dispose();
        Key.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TransportHeaders(TestAgent agent) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AgentsEndpointApiFactory.AddTransportHeaders(request.Headers, agent.ThroughAgentsEndpoint, agent.ClientCertificate);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private CommandAck Execute(SignedMessage message)
    {
        var key = PinnedKeys.SingleOrDefault(k => k.KeyId == message.KeyId);
        if (key is null || !ProtocolCrypto.VerifyMessage(message, AgentProtocol.Purposes.Command, key.PublicKey))
        {
            return new CommandAck(Guid.Empty, AckStatus.InvalidSignature, "command.invalid_signature", null);
        }

        var envelope = ProtocolJson.Deserialize<CommandEnvelope>(message.Payload);
        lock (ReceivedCommands)
        {
            ReceivedCommands.Add(envelope);
        }

        return new CommandAck(envelope.CommandId, AckStatus.Accepted, null, null);
    }
}
