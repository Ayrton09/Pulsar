using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The real agent core (enrollment, authentication, hub connection, command verification, reconnects) running against
/// the panel under test. Only the machine-specific parts are replaced: keys live in memory and the inventory is fixed.
/// </summary>
public sealed class InProcessAgent : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly TestServerTransport _transport;
    private readonly TimeProvider _clock;
    private AgentConnection? _connection;

    public InProcessAgent(WebApplicationFactory<Program> factory, TimeProvider? clock = null)
    {
        _transport = new TestServerTransport(factory.Server);
        _clock = clock ?? TimeProvider.System;
        Configs = new AgentConfigStore(new AgentPaths(_root));
    }

    public AgentConfigStore Configs { get; }

    public MemoryNodeKeyStore Keys { get; } = new();

    public AgentConfig? Config { get; private set; }

    public Guid? CurrentSessionId => _connection?.CurrentSessionId;

    public async Task<AgentConfig> EnrollAsync(string token, string panelKeyFingerprint, string panel = "https://localhost")
    {
        var enrollment = new AgentEnrollment(Configs, Keys, new FixedNodeSystem(), new NoDirectorySecurity(), _transport, _clock);
        Config = await enrollment.EnrollAsync(new EnrollmentRequest(panel, token, panelKeyFingerprint, @"D:\PulsarServers", Development: true),
            TestContext.Current.CancellationToken);
        return Config;
    }

    public Task StartAsync()
    {
        _connection = new AgentConnection(Configs, Keys, new FixedNodeSystem(), _transport, new PanelAuthenticator(_transport),
            new CommandDispatcher(NullLogger<CommandDispatcher>.Instance),
            new AgentConnectionOptions { MetricsInterval = TimeSpan.FromMilliseconds(200), MaxBackoff = TimeSpan.FromSeconds(2) },
            _clock, new StubLifetime(), NullLoggerFactory.Instance);
        return _connection.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Polls until <paramref name="condition"/> holds (the agent works in the background).</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(CancellationToken.None);
            _connection.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public sealed class MemoryNodeKeyStore : INodeKeyStore
    {
        private readonly ConcurrentDictionary<string, ECParameters> _keys = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Names => [.. _keys.Keys];

        public INodeKey Create(string name, NodeKeyScope scope)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys[name] = key.ExportParameters(includePrivateParameters: true);
            return Open(new NodeKeyReference(name, scope, NodeKeyStorage.Software));
        }

        public INodeKey Open(NodeKeyReference reference) =>
            _keys.TryGetValue(reference.Name, out var parameters)
                ? new MemoryKey(reference, ECDsa.Create(parameters))
                : throw new CryptographicException($"Key {reference.Name} does not exist.");

        public void Delete(NodeKeyReference reference) => _keys.TryRemove(reference.Name, out _);

        private sealed class MemoryKey(NodeKeyReference reference, ECDsa key) : INodeKey
        {
            public NodeKeyReference Reference { get; } = reference;

            public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

            public byte[] Sign(ReadOnlySpan<byte> data) => ProtocolCrypto.Sign(key, data);

            public void Dispose() => key.Dispose();
        }
    }

    private sealed class FixedNodeSystem : INodeSystem
    {
        public NodeInventory GetInventory(string? serversRoot) => TestAgent.Inventory with { ServersRoot = serversRoot };

        public NodeMetrics SampleMetrics() =>
            new(DateTimeOffset.UtcNow, 12.5, 4L * 1024 * 1024 * 1024, TestAgent.Inventory.MemoryTotalBytes, TimeSpan.FromHours(3));
    }

    private sealed class NoDirectorySecurity : IAgentDirectorySecurity
    {
        public void Secure(string directory, bool development) => Directory.CreateDirectory(directory);
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => throw new InvalidOperationException("The agent asked to stop.");
    }

    private sealed class ToLocalhost : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "https", Host = "localhost", Port = 443 }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>HTTP and WebSockets straight into the in-memory panel.</summary>
    private sealed class TestServerTransport(TestServer server) : PanelTransport
    {
        /// <summary>Every request lands on https://localhost, whatever origin the agent was configured with (a relay, in effect).</summary>
        public override HttpMessageHandler CreateHttpHandler() => new ToLocalhost { InnerHandler = server.CreateHandler() };

        public override void ConfigureHub(HttpConnectionOptions options)
        {
            options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var token = await options.AccessTokenProvider!();
                var client = server.CreateWebSocketClient();
                client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
                return await client.ConnectAsync(context.Uri, cancellationToken);
            };
        }
    }
}
