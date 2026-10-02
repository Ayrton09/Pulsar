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
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The real agent core (enrollment, authentication, hub connection, command verification, reconnects, server
/// supervision and operations) running against the panel under test. Only the machine-specific parts are replaced: keys
/// live in memory, the inventory is fixed, server processes are scripted and servers get plain folders.
/// </summary>
public sealed class InProcessAgent : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly TestServerTransport _transport;
    private readonly TimeProvider _clock;
    private AgentConnection? _connection;
    private ServerHost? _servers;
    private SteamCmdRunner? _steamCmd;

    public InProcessAgent(WebApplicationFactory<Program> factory, TimeProvider? clock = null)
    {
        _transport = new TestServerTransport(factory.Server);
        _clock = clock ?? TimeProvider.System;
        Configs = new AgentConfigStore(new AgentPaths(_root));
    }

    public AgentConfigStore Configs { get; }

    /// <summary>HTTP and WebSockets into the panel under test (for steps a test performs by hand).</summary>
    public PanelTransport Transport => _transport;

    public MemoryNodeKeyStore Keys { get; } = new();

    public AgentConfig? Config { get; private set; }

    public Guid? CurrentSessionId => _connection?.CurrentSessionId;

    /// <summary>The configuration the running agent uses (key rotations and panel key updates change it).</summary>
    public AgentConfig? LiveConfig => _connection?.Config;

    public AgentConnectionOptions Options { get; } = new() { MetricsInterval = TimeSpan.FromMilliseconds(200), MaxBackoff = TimeSpan.FromSeconds(2) };

    public string ServersRoot => Path.Combine(_root, "servers");

    public ScriptedProcessManager Processes { get; } = new();

    public FolderProvisioner Provisioner { get; } = new();

    /// <summary>The agent's servers while it runs.</summary>
    public ServerHost? Servers => _servers;

    /// <summary>RCON sessions to the scripted game servers (logfile+rcon manifests).</summary>
    public ScriptedRcon Rcon => field ??= new ScriptedRcon(Processes);

    public async Task<AgentConfig> EnrollAsync(string token, string panelKeyFingerprint, string panel = "https://localhost")
    {
        var enrollment = new AgentEnrollment(Configs, Keys, new FixedNodeSystem(), new NoDirectorySecurity(), _transport, _clock);
        Config = await enrollment.EnrollAsync(new EnrollmentRequest(panel, token, panelKeyFingerprint, ServersRoot, Development: true),
            TestContext.Current.CancellationToken);
        return Config;
    }

    public async Task StartAsync()
    {
        // The test game server "files": the install step copies them, the scripted processes check they are there.
        var testGame = Path.Combine(_root, "testgame");
        Directory.CreateDirectory(testGame);
        await File.WriteAllTextAsync(Path.Combine(testGame, "Pulsar.TestGameServer.exe"), "not a real executable", TestContext.Current.CancellationToken);

        var channel = new PanelChannel();
        var install = new InstallOptions { TestGameDirectory = testGame };
        var files = new DirectServerFiles();
        _steamCmd = new SteamCmdRunner(Processes, new ScriptedSteamCmdSource(Path.Combine(_root, "steamcmd.exe")), files,
            new SteamCmdOptions { RetryDelays = [TimeSpan.Zero] }, _clock, NullLogger<SteamCmdRunner>.Instance);
        _servers = new ServerHost(Configs, new ServerStore(Configs.Paths, new PlainStateProtector()), Provisioner, Processes,
            new InstallSteps(install, files), _steamCmd, files, Rcon, install, channel, new SupervisorOptions(), new ServerHostOptions { ShutdownBudget = TimeSpan.FromSeconds(5) }, _clock, NullLoggerFactory.Instance);
        _connection = new AgentConnection(Configs, Keys, new FixedNodeSystem(), _transport, new PanelAuthenticator(_transport),
            new CredentialRotationClient(_transport), new CommandDispatcher(NullLogger<CommandDispatcher>.Instance, _servers), Options,
            _clock, new StubLifetime(), NullLoggerFactory.Instance, channel, _servers);

        // As the agent host orders them: the connection waits for the servers to load; the servers stop first.
        await _connection.StartAsync(TestContext.Current.CancellationToken);
        await _servers.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Stops the agent as a service stop would (the configuration and keys stay).</summary>
    public async Task StopAsync()
    {
        await StopServersAsync();
        if (_connection is not null)
        {
            await _connection.StopAsync(CancellationToken.None);
            _connection.Dispose();
            _connection = null;
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds (the agent works in the background).</summary>
    public static Task EventuallyAsync(Func<Task<bool>> condition, string what) => EventuallyAsync(condition, () => what);

    /// <param name="what">Described when the wait times out (it can report what was last seen).</param>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what()}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopServersAsync();
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

    private async Task StopServersAsync()
    {
        if (_servers is not null)
        {
            await _servers.StopAsync(CancellationToken.None);
            _servers.Dispose();
            _steamCmd?.Dispose();
            _servers = null;
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
