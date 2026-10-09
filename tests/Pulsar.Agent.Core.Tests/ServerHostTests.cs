using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The node's servers and operations (<see cref="ServerHost"/>) with no panel connected: results stay in the outbox.</summary>
public sealed class ServerHostTests : IAsyncDisposable
{
    private const string Manifest = """
        {
          "schemaVersion": 1, "key": "pulsar-test", "version": "1.0.0", "name": "Pulsar test server", "platforms": ["windows-x64"],
          "runtime": { "windows-x64": { "executable": "bin/game.exe", "console": { "mode": "pty" } } },
          "readiness": { "type": "logPattern", "pattern": "^ready", "timeoutSeconds": 60 },
          "stop": { "steps": [ { "type": "consoleCommand", "command": "quit" } ], "timeoutSeconds": 15 }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-host-tests", Guid.NewGuid().ToString("N"));
    private readonly FailingProvisioner _provisioner = new();
    private readonly ServerStore _store;
    private readonly AgentConfigStore _configs;
    private ServerHost? _host;
    private FileCommands? _fileCommands;
    private SteamCmdRunner? _steamCmd;

    public ServerHostTests()
    {
        _configs = new AgentConfigStore(new AgentPaths(_root));
        _configs.Save(new AgentConfig(new Uri("https://panel.example.com/"), Guid.NewGuid(), Guid.NewGuid(),
            new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Software), [], Path.Combine(_root, "servers"),
            DateTimeOffset.UnixEpoch, Development: true, BackupsRoot: Path.Combine(_root, "backups")));
        _store = new ServerStore(_configs.Paths, new PlainStateProtector());
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        _fileCommands?.Dispose();
        _steamCmd?.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnreadableRecordDoesNotKeepTheOtherServersFromLoading()
    {
        var good = Stored("abcd2345");
        File.WriteAllText(Path.Combine(_configs.Paths.ServersStateDirectory, $"{Guid.NewGuid():N}.json"), "{ corrupt");
        File.WriteAllText(Path.Combine(_configs.Paths.OutboxDirectory, $"{Guid.NewGuid():N}.json"), "{ corrupt");

        var host = Host();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(host.Loaded.IsCompletedSuccessfully); // the Hello waits for this
        Assert.Equal(good, Assert.Single(host.Snapshots()).ServerId);
        Assert.Empty(host.OperationsInFlight()); // the Hello's arguments
    }

    [Fact]
    public async Task ASteamLoginLeftInAServersFolderIsDeletedWhenTheAgentStarts()
    {
        // The agent stopped while SteamCMD had it: the game must not find it at its next start.
        Stored("abcd2345");
        var lent = Path.Combine(_root, "servers", "abcd2345", ".pulsar", "steamcmd", "config", "config.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(lent)!);
        File.WriteAllText(lent, "a cached login");

        await Host().StartAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(lent));
    }

    [Fact]
    public async Task ADeleteThatFailedIsFinishedByItsRetry()
    {
        var serverId = Stored("abcd2345");
        var host = Host();
        await host.StartAsync(TestContext.Current.CancellationToken);
        _provisioner.FailNextDeprovision = true;

        var first = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new DeleteServerCommand(serverId), first))).Status);
        Assert.Equal(OperationOutcome.Failed, (await ResultAsync(first)).Outcome);

        var retry = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new DeleteServerCommand(serverId), retry))).Status);
        Assert.Equal(OperationOutcome.Succeeded, (await ResultAsync(retry)).Outcome);
        Assert.Equal(2, _provisioner.Deprovisions);
        Assert.Null(_store.Load(serverId)); // it does not come back at the next start
        Assert.Empty(host.Snapshots());

        // Once really gone, a further retry only reports it.
        var again = Guid.NewGuid();
        Assert.Equal(AckStatus.AlreadyInState, (await host.ExecuteAsync(Envelope(new DeleteServerCommand(serverId), again))).Status);
        Assert.Equal(OperationOutcome.Succeeded, (await ResultAsync(again)).Outcome);
        Assert.Equal(2, _provisioner.Deprovisions);
    }

    [Fact]
    public async Task DeletingAServerWhoseRecordWasSetAsideSaysWhatIsLeftInsteadOfGone()
    {
        // Its record could not be read (corrupt, or protected on another machine): the short id naming its directory and
        // account is inside it.
        var serverId = Guid.NewGuid();
        Directory.CreateDirectory(_configs.Paths.ServersStateDirectory);
        var record = Path.Combine(_configs.Paths.ServersStateDirectory, $"{serverId:N}.json");
        File.WriteAllText(record, "{ corrupt");
        var host = Host();
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(record + ".unreadable"));

        var operation = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new DeleteServerCommand(serverId), operation))).Status);

        var result = await ResultAsync(operation);
        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal("server.record_unreadable", result.ErrorCode);
        Assert.Contains(record + ".unreadable", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, _provisioner.Deprovisions);

        // Cleaned up by hand, the set-aside record with it: the delete then finishes.
        File.Delete(record + ".unreadable");
        var retry = Guid.NewGuid();
        Assert.Equal(AckStatus.AlreadyInState, (await host.ExecuteAsync(Envelope(new DeleteServerCommand(serverId), retry))).Status);
        Assert.Equal(OperationOutcome.Succeeded, (await ResultAsync(retry)).Outcome);
    }

    [Fact]
    public async Task ABackupSentWithAnUnusableTicketIsNotLeftOpen()
    {
        var serverId = Stored("abcd2345");
        var backupId = Guid.NewGuid();
        var archive = new BackupLocation(Path.Combine(_root, "backups"), "abcd2345", backupId).Archive;
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllBytes(archive, [1, 2, 3]);
        var host = Host();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ack = await host.ExecuteAsync(Envelope(new SendBackupCommand(serverId, backupId, "not-a-ticket"), operationId: null));

        Assert.Equal(AckStatus.Rejected, ack.Status);
        Assert.Equal("files.invalid_ticket", ack.ErrorCode);
        File.Delete(archive); // the archive is not held open until a garbage collection
        Assert.False(File.Exists(archive));
    }

    private Guid Stored(string shortId)
    {
        var serverId = Guid.NewGuid();
        Directory.CreateDirectory(_configs.Paths.OutboxDirectory);
        _store.Save(new ServerRecord(serverId, new ServerRuntimeSpec(shortId, Manifest, new Dictionary<string, string?>(), [], AutoStart: false,
            RestartOnCrash: false), DesiredRunning: false, ResumeOnStart: false));
        return serverId;
    }

    private ServerHost Host()
    {
        var channel = new PanelChannel();
        var files = new DirectServerFiles();
        var processes = new FakeProcessManager();
        var install = new InstallOptions();
        _steamCmd = new SteamCmdRunner(processes, new NoSteamCmd(), files, new DirectServerFileManager(), new SteamLoginStore(_configs.Paths, new PlainStateProtector()), new SteamCmdOptions(), TimeProvider.System, NullLogger<SteamCmdRunner>.Instance);
        _fileCommands = new FileCommands(new DirectServerFileManager(), channel, new FileCommandOptions(), NullLogger<FileCommands>.Instance);
        _host = new ServerHost(_configs, _store, _provisioner, processes, new InstallSteps(install, files), _steamCmd, files, new NoRcon(),
            new UnmanagedFirewall(), _fileCommands, new BackupStore(new DirectServerFileManager(), NullLogger<BackupStore>.Instance), install,
            channel, new SupervisorOptions(), new ServerHostOptions { ConsoleLog = null }, TimeProvider.System, NullLoggerFactory.Instance);
        return _host;
    }

    private static CommandEnvelope Envelope(AgentCommand command, Guid? operationId) => new(AgentProtocol.CurrentVersion, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), operationId, null, command);

    private async Task<OperationCompleted> ResultAsync(Guid operationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (_store.PendingResults().FirstOrDefault(r => r.OperationId == operationId) is { } result)
            {
                return result;
            }

            Assert.True(DateTime.UtcNow < deadline, $"No result for operation {operationId}.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FailingProvisioner : IServerProvisioner
    {
        public bool FailNextDeprovision { get; set; }

        public int Deprovisions { get; private set; }

        public ProvisionedServer Provision(string serversRoot, string shortId)
        {
            var server = Describe(serversRoot, shortId);
            Directory.CreateDirectory(server.Directory);
            return server;
        }

        public ProvisionedServer Describe(string serversRoot, string shortId) =>
            new(Path.Combine(serversRoot, shortId), ProcessIdentity.CurrentUser);

        public void Deprovision(string serversRoot, string shortId)
        {
            Deprovisions++;
            if (FailNextDeprovision)
            {
                FailNextDeprovision = false;
                throw new IOException("The account could not be deleted.");
            }
        }
    }

    private sealed class NoSteamCmd : ISteamCmdSource
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => throw new GameProcessException("steamcmd.unavailable", "No SteamCMD here.");
    }

    private sealed class NoRcon : IRconConnector
    {
        public Task<IRconSession> ConnectAsync(RconEndpoint endpoint, CancellationToken cancellationToken) =>
            throw new RconException("No RCON here.");
    }
}
