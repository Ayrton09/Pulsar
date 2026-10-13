using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The real agent core (enrollment, authentication, hub connection, command verification, reconnects, server
/// supervision and operations) running against the panel under test. Only the machine-specific parts are replaced: keys
/// live in memory, the inventory is fixed, server processes are scripted and servers get plain folders.
/// </summary>
public sealed class InProcessAgent : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly PanelTransport _transport;
    private readonly TimeProvider _clock;
    private readonly FixedNodeSystem _system = new();
    private AgentConnection? _connection;
    private ServerHost? _servers;
    private SteamCmdRunner? _steamCmd;

    /// <param name="transport">Another way to the panel than TestServer (the real one, to a panel on Kestrel).</param>
    public InProcessAgent(WebApplicationFactory<Program> factory, TimeProvider? clock = null, PanelTransport? transport = null)
    {
        _transport = transport ?? new TestServerTransport(factory.Server);
        _clock = clock ?? TimeProvider.System;
        Configs = new AgentConfigStore(new AgentPaths(_root));
    }

    public AgentConfigStore Configs { get; }

    /// <summary>HTTP and WebSockets into the panel under test (for steps a test performs by hand).</summary>
    public PanelTransport Transport => _transport;

    public MemoryNodeKeyStore Keys { get; } = new();

    /// <summary>How many of the next node metric samples fail (as a failing Windows call would).</summary>
    public int FailingMetricSamples
    {
        get => _system.FailingSamples;
        set => _system.FailingSamples = value;
    }

    public AgentConfig? Config { get; private set; }

    public Guid? CurrentSessionId => _connection?.CurrentSessionId;

    /// <summary>The address of the live session: the configured one or its fallback.</summary>
    public Uri? CurrentPanel => _connection?.CurrentPanel;

    /// <summary>
    /// The simulated agents' endpoint (<see cref="AgentsEndpointApiFactory"/>) stops answering, as a closed port does.
    /// </summary>
    public bool AgentsEndpointDown
    {
        get => (_transport as TestServerTransport)?.AgentsEndpointDown ?? false;
        set => ((TestServerTransport)_transport).AgentsEndpointDown = value;
    }

    /// <summary>
    /// What stands between the agent's HTTP requests and the panel (a TLS-terminating proxy, in effect): it gets each
    /// request and the way to pass it on, and answers in its place. Null passes everything on.
    /// </summary>
    public Func<HttpRequestMessage, Func<Task<HttpResponseMessage>>, Task<HttpResponseMessage>>? Proxy
    {
        get => ((TestServerTransport)_transport).Proxy;
        set => ((TestServerTransport)_transport).Proxy = value;
    }

    /// <summary>The configuration the running agent uses (key rotations and panel key updates change it).</summary>
    public AgentConfig? LiveConfig => _connection?.Config;

    public AgentConnectionOptions Options { get; } = new() { MetricsInterval = TimeSpan.FromMilliseconds(200), MaxBackoff = TimeSpan.FromSeconds(2) };

    public string ServersRoot => Path.Combine(_root, "servers");

    public ScriptedProcessManager Processes { get; } = new();

    public FolderProvisioner Provisioner { get; } = new();

    /// <summary>File transfer timeouts (tests shorten them).</summary>
    public FileCommandOptions FileOptions { get; } = new();

    /// <summary>The agent's remote storage (in memory).</summary>
    public MemoryRemoteBackups RemoteBackups { get; } = new();

    /// <summary>The agent's servers while it runs.</summary>
    public ServerHost? Servers => _servers;

    /// <summary>What the scripted game servers answer to their manifest's query.</summary>
    /// <summary>The logins SteamCMD cached per Steam account (in this agent's state directory).</summary>
    public SteamLoginStore SteamLogins => field ??= new SteamLoginStore(Configs.Paths, new PlainStateProtector());

    public ScriptedQuery Query => field ??= new ScriptedQuery(_clock);

    /// <summary>The running agent's hub connection, for messages a test sends as the agent.</summary>
    public PanelChannel? Channel { get; private set; }

    /// <summary>The agent's downloads (addons, Minecraft): set before <see cref="StartAsync"/> to fake the sites; null for the real ones.</summary>
    public HttpMessageHandler? Http { get; set; }

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

        var channel = Channel = new PanelChannel();
        var install = Http is null ? new InstallOptions { TestGameDirectory = testGame } : new InstallOptions { TestGameDirectory = testGame, HttpHandler = Http };
        var files = new DirectServerFiles();
        _steamCmd = new SteamCmdRunner(Processes, new ScriptedSteamCmdSource(Path.Combine(_root, "steamcmd.exe")), files, new DirectServerFileManager(), SteamLogins,
            new SteamCmdOptions { RetryDelays = [TimeSpan.Zero] }, _clock, NullLogger<SteamCmdRunner>.Instance);
        _servers = new ServerHost(Configs, new ServerStore(Configs.Paths, new PlainStateProtector()), Provisioner, Processes,
            new InstallSteps(install, files), _steamCmd, files, Rcon, new UnmanagedFirewall(),
            new FileCommands(new DirectServerFileManager(), channel, FileOptions, NullLogger<FileCommands>.Instance),
            new BackupStore(new DirectServerFileManager(), NullLogger<BackupStore>.Instance, RemoteBackups), install, channel, new SupervisorOptions(), new ServerHostOptions { ShutdownBudget = TimeSpan.FromSeconds(5), MetricsInterval = TimeSpan.FromMilliseconds(200) }, _clock, NullLoggerFactory.Instance, Query);
        _connection = new AgentConnection(Configs, Keys, _system, _transport, new PanelAuthenticator(_transport),
            new CredentialRotationClient(_transport), new CommandDispatcher(NullLogger<CommandDispatcher>.Instance, _servers, SteamLogins), Options,
            _clock, new StubLifetime(), NullLoggerFactory.Instance, channel, _servers, SteamLogins);

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

        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _held = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Names => [.. _keys.Keys];

        /// <summary>Creating a key fails (as a key storage provider that refuses would).</summary>
        public bool RefuseCreate { get; set; }

        /// <summary>Keys created from now on wait for it before their first signature (to stop the agent in the middle of a rotation).</summary>
        public ManualResetEventSlim? HoldSigning { get; set; }

        /// <summary>Set when a held key starts waiting to sign.</summary>
        public ManualResetEventSlim SigningHeld { get; } = new();

        /// <summary>Keys created from now on cannot be opened again (a key storage provider that loses them).</summary>
        public bool LoseCreatedKeys { get; set; }

        /// <summary>How many keys were created.</summary>
        public int Created => Volatile.Read(ref _created);

        private int _created;

        private readonly ConcurrentDictionary<string, bool> _lost = new(StringComparer.Ordinal);

        public INodeKey Create(string name, NodeKeyScope scope)
        {
            if (RefuseCreate)
            {
                throw new CryptographicException("The key storage provider refused to create a key.");
            }

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys[name] = key.ExportParameters(includePrivateParameters: true);
            Interlocked.Increment(ref _created);
            if (HoldSigning is { } hold)
            {
                _held[name] = hold;
            }

            var created = Open(new NodeKeyReference(name, scope, NodeKeyStorage.Software));
            if (LoseCreatedKeys)
            {
                _lost[name] = true;
            }

            return created;
        }

        public INodeKey Open(NodeKeyReference reference) =>
            _keys.TryGetValue(reference.Name, out var parameters) && !_lost.ContainsKey(reference.Name)
                ? new MemoryKey(reference, ECDsa.Create(parameters), this)
                : throw new CryptographicException($"Key {reference.Name} does not exist.");

        public void Delete(NodeKeyReference reference) => _keys.TryRemove(reference.Name, out _);

        private sealed class MemoryKey(NodeKeyReference reference, ECDsa key, MemoryNodeKeyStore store) : INodeKey
        {
            public NodeKeyReference Reference { get; } = reference;

            public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

            public byte[] Sign(ReadOnlySpan<byte> data)
            {
                if (store._held.TryRemove(Reference.Name, out var hold))
                {
                    store.SigningHeld.Set();
                    hold.Wait(TimeSpan.FromSeconds(30));
                }

                return ProtocolCrypto.Sign(key, data);
            }

            // Loaded back from a PFX: SChannel signs a TLS handshake only with a key in a key container (KestrelPanelFactory).
            public X509Certificate2 CreateCertificate()
            {
                using var certificate = NodeCertificate.Create(key);
                return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "node"), "node");
            }

            public void Dispose() => key.Dispose();
        }
    }

    private sealed class FixedNodeSystem : INodeSystem
    {
        private int _failingSamples;

        public int FailingSamples
        {
            get => Volatile.Read(ref _failingSamples);
            set => Volatile.Write(ref _failingSamples, value);
        }

        public NodeInventory GetInventory(string? serversRoot) => TestAgent.Inventory with { ServersRoot = serversRoot };

        public NodeMetrics SampleMetrics() => Interlocked.Decrement(ref _failingSamples) >= 0
            ? throw new System.ComponentModel.Win32Exception(5)
            : new(DateTimeOffset.UtcNow, 12.5, 4L * 1024 * 1024 * 1024, TestAgent.Inventory.MemoryTotalBytes, TimeSpan.FromHours(3));
    }

    private sealed class NoDirectorySecurity : IAgentDirectorySecurity
    {
        public void SecureState(string directory, IReadOnlyCollection<string> ownFolders, bool development) => Directory.CreateDirectory(directory);

        public void SecureFolder(string directory, bool development) => Directory.CreateDirectory(directory);

        public void SecureServersRoot(string directory, bool development) => Directory.CreateDirectory(directory);
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => throw new InvalidOperationException("The agent asked to stop.");
    }

    /// <summary>
    /// Every request lands on https://localhost, whatever origin the agent was configured with (a relay, in effect),
    /// except the simulated agents' endpoint: its port stays, and the client certificate travels as test headers.
    /// </summary>
    private sealed class ToLocalhost(TestServerTransport transport) : DelegatingHandler
    {
        public X509Certificate2? Certificate { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var agentsEndpoint = request.RequestUri!.Port == AgentsEndpointApiFactory.AgentsPort;
            if (agentsEndpoint && transport.AgentsEndpointDown)
            {
                throw new HttpRequestException("No connection could be made: the agents' endpoint is down (test).");
            }

            request.RequestUri = new UriBuilder(request.RequestUri) { Scheme = "https", Host = "localhost", Port = agentsEndpoint ? request.RequestUri.Port : 443 }.Uri;
            if (agentsEndpoint)
            {
                AgentsEndpointApiFactory.AddTransportHeaders(request.Headers, true, Certificate);
            }

            return transport.Proxy is { } proxy ? proxy(request, () => base.SendAsync(request, cancellationToken)) : base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>HTTP and WebSockets straight into the in-memory panel.</summary>
    private sealed class TestServerTransport(TestServer server) : PanelTransport
    {
        public bool AgentsEndpointDown { get; set; }

        public Func<HttpRequestMessage, Func<Task<HttpResponseMessage>>, Task<HttpResponseMessage>>? Proxy { get; set; }

        public override HttpMessageHandler CreateHttpHandler() => new ToLocalhost(this) { InnerHandler = server.CreateHandler() };

        public override HttpMessageHandler WithClientCertificate(HttpMessageHandler handler, X509Certificate2 certificate)
        {
            ((ToLocalhost)handler).Certificate = certificate;
            return handler;
        }

        public override void ConfigureHub(HttpConnectionOptions options)
        {
            options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var agentsEndpoint = context.Uri.Port == AgentsEndpointApiFactory.AgentsPort;
                if (agentsEndpoint && AgentsEndpointDown)
                {
                    throw new HttpRequestException("No connection could be made: the agents' endpoint is down (test).");
                }

                var token = await options.AccessTokenProvider!();
                var certificate = options.ClientCertificates?.OfType<X509Certificate2>().FirstOrDefault();
                var client = server.CreateWebSocketClient();
                client.ConfigureRequest = request =>
                {
                    request.Headers.Authorization = $"Bearer {token}";
                    if (agentsEndpoint)
                    {
                        AgentsEndpointApiFactory.AddTransportHeaders(request.Headers, true, certificate);
                    }
                };
                return await client.ConnectAsync(context.Uri, cancellationToken);
            };
        }
    }
}

/// <summary>Answers every query with <see cref="Answer"/>, as a game whose query port answers would.</summary>
public sealed class ScriptedQuery(TimeProvider clock) : IServerQuery
{
    public Func<QueryEndpoint, ServerQueryInfo?> Answer { get; set; } = _ => null;

    public ServerQueryInfo Info(string name, string map, params string[] players) =>
        new(clock.GetUtcNow(), name, map, players.Length, 16, 0, "1.0", [.. players.Select(p => new QueryPlayer(p, 0, TimeSpan.FromMinutes(1)))]);

    public Task<ServerQueryInfo> QueryAsync(QueryEndpoint endpoint, CancellationToken cancellationToken) =>
        Answer(endpoint) is { } info ? Task.FromResult(info with { At = clock.GetUtcNow() }) : throw new QueryException("No answer.");
}
