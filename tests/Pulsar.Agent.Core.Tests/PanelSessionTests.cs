using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// The agent's sessions against a fake panel that misbehaves (docs/architecture/03): its sign-in works, and its hub
/// keeps the connection alive but never answers Hello, or hands out a session already due for renewal.
/// </summary>
public sealed class PanelSessionTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-session-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePanel _panel = new();
    private AgentConnection? _connection;

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(CancellationToken.None);
            _connection.Dispose();
        }

        await _panel.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task APanelThatNeverAnswersHelloIsGivenUpOnAndConnectedToAgain()
    {
        _panel.AnswersHello = false;
        await StartAsync(new AgentConnectionOptions { SessionStartTimeout = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(1) });

        await EventuallyAsync(() => _panel.Hellos >= 2, () => $"{_panel.Hellos} Hello(s)");
        Assert.Null(_connection!.CurrentSessionId);
    }

    [Fact]
    public async Task ASessionDueForRenewalAsSoonAsItOpensIsNotRenewedInALoop()
    {
        // Its token already expired by the panel's clock: renewing it at once would sign in again and again.
        _panel.SessionLifetime = TimeSpan.FromMinutes(-1);
        await StartAsync(new AgentConnectionOptions { MinimumRenewalDelay = TimeSpan.FromMilliseconds(200), MaxBackoff = TimeSpan.FromSeconds(2) });

        await EventuallyAsync(() => _panel.Hellos >= 1, () => "no Hello");
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Each renewal waits its floor, then a backoff that grows (at least 0.5 s, then 1 s): a handful at most.
        Assert.InRange(_panel.Hellos, 2, 5);
        Assert.NotNull(_connection!.CurrentSessionId); // the session in hand goes on meanwhile
    }

    [Fact]
    public async Task APendingKeyAnOlderPanelAnswersWithABareUnauthorizedDoesNotKeepTheAgentOffline()
    {
        // An older panel refuses an unknown credential with a 401 that has no error body, which reads as "not the panel".
        var pending = new PendingRotation(new NodeKeyReference("PulsarAgent-2", NodeKeyScope.Machine, NodeKeyStorage.Software), Guid.NewGuid(), DateTimeOffset.UtcNow);
        _panel.BareUnauthorizedFor = pending.CredentialId;

        await StartAsync(new AgentConnectionOptions(), pending);

        await EventuallyAsync(() => _connection!.CurrentSessionId is not null, () => $"no session after {_panel.PendingAttempts} attempt(s) with the pending key");
        Assert.NotNull(_connection!.CurrentSessionId);
        Assert.NotNull(_connection!.Config!.PendingRotation); // one such answer is not yet a reason to give the key up
    }

    [Fact]
    public async Task APendingKeyThePanelKeepsAnsweringWithoutItsErrorIsAbandonedForTheCurrentOne()
    {
        var pending = new PendingRotation(new NodeKeyReference("PulsarAgent-2", NodeKeyScope.Machine, NodeKeyStorage.Software), Guid.NewGuid(), DateTimeOffset.UtcNow);
        _panel.BareUnauthorizedFor = pending.CredentialId;
        _panel.SessionLifetime = TimeSpan.FromMinutes(-1); // every session is due for renewal: sign-ins follow each other

        await StartAsync(new AgentConnectionOptions { MinimumRenewalDelay = TimeSpan.FromMilliseconds(100), MaxBackoff = TimeSpan.FromSeconds(1) }, pending);

        await EventuallyAsync(() => _connection!.Config is { PendingRotation: null }, () => $"pending key kept after {_panel.PendingAttempts} attempt(s)");
        var attempts = _panel.PendingAttempts;
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(attempts, _panel.PendingAttempts); // not tried again
        Assert.NotNull(_connection!.CurrentSessionId);
    }

    private async Task StartAsync(AgentConnectionOptions options, PendingRotation? pending = null)
    {
        var configs = new AgentConfigStore(new AgentPaths(_root));
        var keys = new MemoryKeys();
        using (keys.Create("PulsarAgent-1", NodeKeyScope.Machine))
        {
        }

        if (pending is not null)
        {
            using (keys.Create(pending.Key.Name, NodeKeyScope.Machine))
            {
            }
        }

        configs.Save(new AgentConfig(FakePanel.Address, Guid.NewGuid(), Guid.NewGuid(), new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine,
            NodeKeyStorage.Software), [], Path.Combine(_root, "servers"), DateTimeOffset.UtcNow, Development: true, KeyCreatedAt: DateTimeOffset.UtcNow,
            PendingRotation: pending));
        options.MetricsInterval = TimeSpan.FromMinutes(1);
        _connection = new AgentConnection(configs, keys, new FixedSystem(), _panel, new PanelAuthenticator(_panel), new CredentialRotationClient(_panel),
            new CommandDispatcher(NullLogger<CommandDispatcher>.Instance), options, TimeProvider.System, new StubLifetime(), NullLoggerFactory.Instance);
        await _connection.StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task EventuallyAsync(Func<bool> condition, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out: {what()}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Signs any node in, and serves its hub over a loopback socket in the SignalR JSON protocol: the handshake, a ping every
    /// 200 ms, and Hello's answer when <see cref="AnswersHello"/>.
    /// </summary>
    private sealed class FakePanel : PanelTransport, IAsyncDisposable
    {
        public static readonly Uri Address = new("https://panel.test/");

        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _served = [];
        private int _hellos;
        private int _pendingAttempts;

        public bool AnswersHello { get; set; } = true;

        public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(15);

        public int Hellos => Volatile.Read(ref _hellos);

        /// <summary>A credential refused with a bare 401, without the protocol's error body (what an older panel does).</summary>
        public Guid? BareUnauthorizedFor { get; set; }

        /// <summary>Challenges asked for <see cref="BareUnauthorizedFor"/>.</summary>
        public int PendingAttempts => Volatile.Read(ref _pendingAttempts);

        public override HttpMessageHandler CreateHttpHandler() => new Handler(this);

        public override HttpMessageHandler WithClientCertificate(HttpMessageHandler handler, X509Certificate2 certificate) => handler;

        public override void ConfigureHub(HttpConnectionOptions options) => options.WebSocketFactory = async (_, cancellationToken) =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, cancellationToken);
            var server = await listener.AcceptTcpClientAsync(cancellationToken);
            var serverSocket = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, TimeSpan.Zero);
            _served.Add(ServeAsync(serverSocket, server));
            return WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, TimeSpan.Zero);
        };

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            foreach (var served in _served)
            {
                await served.ContinueWith(_ => { }, TaskScheduler.Default);
            }

            _stop.Dispose();
        }

        private async Task ServeAsync(WebSocket socket, TcpClient client)
        {
            using var _ = client;
            using var sending = new SemaphoreSlim(1, 1);
            async Task SendAsync(string record)
            {
                await sending.WaitAsync(_stop.Token);
                try
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(record + "\u001e"), WebSocketMessageType.Text, true, _stop.Token);
                }
                finally
                {
                    sending.Release();
                }
            }

            try
            {
                var buffer = new byte[64 * 1024];
                var received = new StringBuilder();
                var handshaken = false;
                Task? pinging = null;
                while (!_stop.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    received.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    var text = received.ToString();
                    var end = text.LastIndexOf('\u001e');
                    if (end < 0)
                    {
                        continue;
                    }

                    received.Remove(0, end + 1);
                    foreach (var record in text[..end].Split('\u001e'))
                    {
                        if (!handshaken)
                        {
                            handshaken = true;
                            await SendAsync("{}");
                            pinging = PingAsync(SendAsync);
                            continue;
                        }

                        using var message = JsonDocument.Parse(record);
                        if (message.RootElement.GetProperty("type").GetInt32() == 1
                            && message.RootElement.GetProperty("target").GetString() == nameof(IAgentHub.Hello))
                        {
                            Interlocked.Increment(ref _hellos);
                            if (AnswersHello && message.RootElement.TryGetProperty("invocationId", out var id))
                            {
                                var accepted = new HelloAccepted(DateTimeOffset.UtcNow, []);
                                await SendAsync(JsonSerializer.Serialize(new { type = 3, invocationId = id.GetString(), result = accepted }, ProtocolJson.Options));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // The agent or the test closed it.
            }
        }

        private async Task PingAsync(Func<string, Task> send)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(200, _stop.Token);
                    await send("{\"type\":6}");
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // Closed.
            }
        }

        private HttpResponseMessage Answer(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
        {
            AgentProtocol.ChallengePath => Json(new ChallengeResponse(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), PanelAddress.Origin(Address))),
            AgentProtocol.TokenPath => Json(new TokenResponse("session-token", Guid.NewGuid(), DateTimeOffset.UtcNow + SessionLifetime)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ProtocolJson.Options) };

        private sealed class Handler(FakePanel panel) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (panel.BareUnauthorizedFor is { } refused && request.RequestUri!.AbsolutePath == AgentProtocol.ChallengePath
                    && await request.Content!.ReadFromJsonAsync<ChallengeRequest>(ProtocolJson.Options, cancellationToken) is { } challenge
                    && challenge.CredentialId == refused)
                {
                    Interlocked.Increment(ref panel._pendingAttempts);
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return panel.Answer(request);
            }
        }
    }

    private sealed class MemoryKeys : INodeKeyStore
    {
        private readonly ConcurrentDictionary<string, ECParameters> _keys = new(StringComparer.Ordinal);

        public INodeKey Create(string name, NodeKeyScope scope)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys[name] = key.ExportParameters(includePrivateParameters: true);
            return Open(new NodeKeyReference(name, scope, NodeKeyStorage.Software));
        }

        public INodeKey Open(NodeKeyReference reference) => new Key(reference, ECDsa.Create(_keys[reference.Name]));

        public void Delete(NodeKeyReference reference) => _keys.TryRemove(reference.Name, out _);

        private sealed class Key(NodeKeyReference reference, ECDsa key) : INodeKey
        {
            public NodeKeyReference Reference { get; } = reference;

            public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

            public byte[] Sign(ReadOnlySpan<byte> data) => ProtocolCrypto.Sign(key, data);

            public X509Certificate2 CreateCertificate() => NodeCertificate.Create(key);

            public void Dispose() => key.Dispose();
        }
    }

    private sealed class FixedSystem : INodeSystem
    {
        public NodeInventory GetInventory(string? serversRoot) =>
            new("FLEET-01", "Windows 11 Pro", "26200", "Test CPU", 8, 16L * 1024 * 1024 * 1024, serversRoot);

        public NodeMetrics SampleMetrics() => throw new NotSupportedException();
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
