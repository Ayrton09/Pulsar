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
    private readonly FakeProcessManager _games = new();
    private readonly HookedServerFileManager _backupFiles = new(new DirectServerFileManager()); // what backups read and write through
    private InProcessHostLauncher? _launcher;

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
        _games.Gate?.TrySetResult(); // a test that failed while a start waited
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        _fileCommands?.Dispose();
        _steamCmd?.Dispose();
        if (_launcher is not null)
        {
            await _launcher.DisposeAsync();
        }

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
    public async Task AWantedStateThatCannotBeSavedDoesNotFailTheCommandAndIsSavedLater()
    {
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true, new ServerHostOptions { ConsoleLog = null, MetricsInterval = TimeSpan.FromMilliseconds(50) });
        await host.StartAsync(TestContext.Current.CancellationToken);
        var record = Path.Combine(_configs.Paths.ServersStateDirectory, $"{serverId:N}.json");

        // A scanner holding the record keeps it from being replaced (a full disk does the same).
        using (new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null))).Status);
            await StartedAsync();
            Assert.False(_store.Load(serverId)!.DesiredRunning);
        }

        await WaitUntilAsync(() => _store.Load(serverId)?.DesiredRunning == true);
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
    public async Task AGameInAHostKeepsRunningWhileTheAgentRestartsAndItsConsoleGoesOn()
    {
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null))).Status);
        var game = await StartedAsync();
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);
        game.Print("line 1\n");
        await WaitUntilAsync(() => Text(host, serverId).Contains("line 1", StringComparison.Ordinal));

        await RestartAgentAsync(); // the agent stops: the game does not
        Assert.False(game.Disposed);
        Assert.Empty(game.Input);
        var record = Assert.Single(_store.LoadHosts());
        Assert.Equal((serverId, true, 13L), (record.ServerId, record.Ready, record.ConsoleOffset));
        game.Print("while away\n");

        host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Running, Assert.Single(host.Snapshots()).State); // what the first Hello reports
        await WaitUntilAsync(() => Text(host, serverId).Contains("while away", StringComparison.Ordinal));
        Assert.DoesNotContain("line 1", Text(host, serverId), StringComparison.Ordinal); // the panel has it already
        Assert.Single(_games.Started); // taken over, not started again

        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new SendConsoleInputCommand(serverId, "say hi"), null))).Status);
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StopServerCommand(serverId), null))).Status);
        await WaitForAsync(host, serverId, RuntimeState.Offline);
        Assert.Equal(["say hi\r\n", "quit\r\n"], game.Input);
        await WaitUntilAsync(() => game.Disposed && _store.LoadHosts().Count == 0);
    }

    [Fact]
    public async Task AGameThatCrashedWhileTheAgentWasAwayIsHandledWhenItComesBack()
    {
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));
        var game = await StartedAsync();
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);

        await RestartAgentAsync();
        game.Exit(5);

        host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(host, serverId, RuntimeState.Crashed);
        await WaitUntilAsync(() => _store.LoadHosts().Count == 0);
        Assert.Single(_games.Started); // no restart: the server does not restart on crashes
    }

    [Fact]
    public async Task AnUnenrollStopsTheServersInsteadOfLeavingThemRunning()
    {
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));
        var game = await StartedAsync();
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);
        File.WriteAllText(_configs.Paths.StopServersMarker, "");

        await RestartAgentAsync();

        Assert.Equal(["quit\r\n"], game.Input);
        await WaitUntilAsync(() => game.Disposed);
        Assert.Empty(_store.LoadHosts());
        Assert.True(_store.Load(serverId)!.ResumeOnStart);
        Assert.False(File.Exists(_configs.Paths.StopServersMarker)); // asked once
    }

    [Fact]
    public async Task AHostWhoseServerIsGoneIsKilledWhenTheAgentStarts()
    {
        _launcher = new InProcessHostLauncher(_games);
        var orphan = _launcher.Launch(Guid.NewGuid(), GameHostProtocol.NewPipeName(Guid.NewGuid()));
        _store.SaveHost(new GameHostRecord(Guid.NewGuid(), orphan, 0, 0, Ready: true));

        await Host(hosted: true).StartAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => !_launcher.IsAlive(orphan));
        Assert.Empty(_store.LoadHosts());
    }

    [Fact]
    public async Task AHostIsRecordedBeforeItsGameStarts()
    {
        // Starting the game can take seconds (an account's first logon): an agent that dies meanwhile must find the host.
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        _games.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));

        await WaitUntilAsync(() => _store.LoadHosts().Count == 1);
        var record = Assert.Single(_store.LoadHosts());
        Assert.Equal((serverId, false, 0L), (record.ServerId, record.Ready, record.ConsoleOffset));
        Assert.Empty(_games.Started);
        Assert.True(_launcher!.IsAlive(record.Host));

        _games.Gate.SetResult();
        var game = await StartedAsync();
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);
        Assert.Equal(record.Host, Assert.Single(_store.LoadHosts()).Host);
    }

    [Fact]
    public async Task AHostWhoseGameCouldNotStartIsNotRecorded()
    {
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        _games.FailNext = new GameProcessException("process.invalid_executable", "Only .exe files can be started.");

        await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));

        await WaitForAsync(host, serverId, RuntimeState.Crashed);
        Assert.Empty(_store.LoadHosts());
        await WaitUntilAsync(() => _launcher!.Launched.All(h => !_launcher.IsAlive(h)));
    }

    [Fact]
    public async Task AServerThatCannotBeLoadedKeepsTheGameItsHostRuns()
    {
        _launcher = new InProcessHostLauncher(_games);
        // One whose record names a manifest this agent cannot use, one whose record cannot be read at all.
        var unloadable = Stored("abcd2345", manifest: "{}");
        var unreadable = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_configs.Paths.ServersStateDirectory, $"{unreadable:N}.json"), "{ corrupt");
        var hostsOf = new Dictionary<Guid, GameHostReference>();
        foreach (var serverId in new[] { unloadable, unreadable })
        {
            hostsOf[serverId] = _launcher.Launch(serverId, GameHostProtocol.NewPipeName(serverId));
            _store.SaveHost(new GameHostRecord(serverId, hostsOf[serverId], 0, 0, Ready: true));
        }

        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(host.Snapshots());
        Assert.All(hostsOf.Values, h => Assert.True(_launcher.IsAlive(h)));
        Assert.Equal(2, _store.LoadHosts().Count);
    }

    [Fact]
    public async Task AGameStillStartingWhenTheAgentStoppedIsCheckedForReadinessAgain()
    {
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));
        var game = await StartedAsync();
        await WaitForAsync(host, serverId, RuntimeState.Starting);

        await RestartAgentAsync();
        Assert.False(Assert.Single(_store.LoadHosts()).Ready);

        host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Starting, Assert.Single(host.Snapshots()).State);
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);
        await WaitUntilAsync(() => _store.LoadHosts().SingleOrDefault()?.Ready == true);
        Assert.Single(_games.Started);
    }

    [Fact]
    public async Task AfterACrashOfTheAgentTheConsoleRepeatsAtMostWhatCameSinceTheLastCheckpoint()
    {
        var options = new ServerHostOptions { ConsoleLog = null, HostCheckpointInterval = TimeSpan.FromMilliseconds(50) };
        var serverId = Stored("abcd2345");
        var host = Host(hosted: true, options);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));
        var game = await StartedAsync();
        game.Print("ready\n");
        game.Print("line 1\n");
        await WaitUntilAsync(() => Text(host, serverId).Contains("line 1", StringComparison.Ordinal));

        // Noted while the agent runs, without any stop.
        await WaitUntilAsync(() => _store.LoadHosts().SingleOrDefault()?.ConsoleOffset == 13);
        var checkpoint = Assert.Single(_store.LoadHosts());
        game.Print("line 2\n");
        await WaitUntilAsync(() => Text(host, serverId).Contains("line 2", StringComparison.Ordinal));

        // The agent dies right after that checkpoint: its next run finds what it had noted then.
        await RestartAgentAsync();
        _store.SaveHost(checkpoint);
        host = Host(hosted: true, options);
        await host.StartAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => Text(host, serverId).Contains("line 2", StringComparison.Ordinal));
        Assert.DoesNotContain("line 1", Text(host, serverId), StringComparison.Ordinal);
        Assert.Equal(RuntimeState.Running, Assert.Single(host.Snapshots()).State);
    }

    [Fact]
    public async Task AServerStoppingWhenTheAgentStopsFinishesItsStopInsteadOfBeingLeft()
    {
        var serverId = Stored("abcd2345", manifest: Manifest.Replace("\"timeoutSeconds\": 15", "\"timeoutSeconds\": 1", StringComparison.Ordinal));
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));
        var game = await StartedAsync();
        game.IgnoreQuit = true;
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);
        await host.ExecuteAsync(Envelope(new StopServerCommand(serverId), null));
        await WaitForAsync(host, serverId, RuntimeState.Stopping);
        await WaitUntilAsync(() => _store.LoadHosts().SingleOrDefault()?.Stopping == true); // for an agent that dies now

        await RestartAgentAsync();

        Assert.Equal(1, game.TerminatedWith); // its stop timed out
        await WaitUntilAsync(() => game.Disposed);
        Assert.Empty(_store.LoadHosts());
    }

    [Fact]
    public async Task AGameInAHostOfAnotherProtocolVersionIsLeftRunningUntilTheServerIsStopped()
    {
        _launcher = new InProcessHostLauncher(_games, new GameHostOptions { Protocol = GameHostProtocol.Version + 1 });
        var serverId = Stored("abcd2345", autoStart: true);
        var stranded = _launcher.Launch(serverId, GameHostProtocol.NewPipeName(serverId));
        _store.SaveHost(new GameHostRecord(serverId, stranded, 0, 0, Ready: true));

        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(_launcher.IsAlive(stranded));
        Assert.Equal(stranded, Assert.Single(_store.LoadHosts()).Host); // for an agent that speaks its protocol
        Assert.Equal(RuntimeState.Offline, Assert.Single(host.Snapshots()).State);
        Assert.Empty(_games.Started); // not started beside it, although it starts with the agent
        var restart = await host.ExecuteAsync(Envelope(new RestartServerCommand(serverId), null));
        Assert.Equal((AckStatus.Rejected, "server.host_incompatible"), (restart.Status, restart.ErrorCode));

        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StopServerCommand(serverId), null))).Status);

        await _launcher.WaitEndedAsync(stranded);
        Assert.Empty(_store.LoadHosts());
        Assert.Contains("ended the game", Text(host, serverId), StringComparison.Ordinal);
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null))).Status);
    }

    [Fact]
    public async Task StartingAServerWhoseGameRunsInAHostOfAnotherProtocolVersionEndsThatHostFirst()
    {
        // The panel shows the server offline (this agent cannot drive that game), so Start is what a user can press.
        _launcher = new InProcessHostLauncher(_games);
        var serverId = Stored("abcd2345");
        var stranded = _launcher.Launch(serverId, GameHostProtocol.NewPipeName(serverId),
            new GameHostOptions { Protocol = GameHostProtocol.Version + 1 });
        _store.SaveHost(new GameHostRecord(serverId, stranded, 0, 0, Ready: true));
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Offline, Assert.Single(host.Snapshots()).State);

        var start = await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));

        Assert.Equal(AckStatus.Accepted, start.Status);
        await _launcher.WaitEndedAsync(stranded);
        var game = await StartedAsync();
        Assert.Single(_games.Started);
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);
        Assert.NotEqual(stranded, Assert.Single(_store.LoadHosts()).Host); // the new game's host
        // The user learns where the old game went: it was killed, not stopped (its console belongs to that host).
        Assert.Contains("ended the game", Text(host, serverId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatCannotBeCheckedIsNotTakenForGoneAndTheServerIsNotStartedBesideIt()
    {
        // OpenProcess failing with anything but "no such process" (access denied, say) tells nothing about the host.
        _launcher = new InProcessHostLauncher(_games);
        var serverId = Stored("abcd2345", autoStart: true);
        var running = _launcher.Launch(serverId, GameHostProtocol.NewPipeName(serverId));
        _store.SaveHost(new GameHostRecord(serverId, running, 0, 0, Ready: true));
        _launcher.Unverifiable = true;

        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(running, Assert.Single(_store.LoadHosts()).Host); // its record stays
        Assert.Empty(_games.Started); // not started beside it, although it starts with the agent
        var start = await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));
        Assert.Equal((AckStatus.Rejected, "server.host_not_ended"), (start.Status, start.ErrorCode));
        Assert.Empty(_games.Started);
        Assert.Equal(running, Assert.Single(_store.LoadHosts()).Host);

        // Once the host can be checked again, ending it is what a start does first.
        _launcher.Unverifiable = false;
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null))).Status);
        await _launcher.WaitEndedAsync(running);
    }

    [Fact]
    public async Task AHostThatSurvivesItsKillKeepsItsRecordAndTheStartIsRefused()
    {
        _launcher = new InProcessHostLauncher(_games);
        var serverId = Stored("abcd2345");
        var stranded = _launcher.Launch(serverId, GameHostProtocol.NewPipeName(serverId),
            new GameHostOptions { Protocol = GameHostProtocol.Version + 1 });
        _store.SaveHost(new GameHostRecord(serverId, stranded, 0, 0, Ready: true));
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        _launcher.KillFails = true;

        var start = await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));

        Assert.Equal((AckStatus.Rejected, "server.host_not_ended"), (start.Status, start.ErrorCode));
        Assert.Empty(_games.Started);
        Assert.Equal(stranded, Assert.Single(_store.LoadHosts()).Host);
        var stop = await host.ExecuteAsync(Envelope(new StopServerCommand(serverId), null));
        Assert.Equal((AckStatus.Rejected, "server.host_not_ended"), (stop.Status, stop.ErrorCode));
        Assert.Equal(stranded, Assert.Single(_store.LoadHosts()).Host);

        _launcher.KillFails = false;
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null))).Status);
        await _launcher.WaitEndedAsync(stranded);
    }

    [Fact]
    public async Task HostsAnEarlierRunLeftAreEndedWhenGamesRunInTheAgent()
    {
        _launcher = new InProcessHostLauncher(_games);
        var serverId = Stored("abcd2345", autoStart: true);
        var left = _launcher.Launch(serverId, GameHostProtocol.NewPipeName(serverId));
        _store.SaveHost(new GameHostRecord(serverId, left, 0, 0, Ready: true));

        var host = Host(hosted: true, new ServerHostOptions { ConsoleLog = null, GamesInHosts = false });
        await host.StartAsync(TestContext.Current.CancellationToken);

        await _launcher.WaitEndedAsync(left);
        Assert.Empty(_store.LoadHosts());
        await StartedAsync(); // started again, in the agent: not beside the old one
        Assert.Single(_games.Started);
        Assert.DoesNotContain(AgentCapabilities.ServerHosts, host.Capabilities);
    }

    [Fact]
    public async Task ARequestToStopTheServersLeftByAnEarlierRunIsDeleted()
    {
        Stored("abcd2345");
        Directory.CreateDirectory(Path.GetDirectoryName(_configs.Paths.StopServersMarker)!);
        File.WriteAllText(_configs.Paths.StopServersMarker, "");
        File.SetLastWriteTimeUtc(_configs.Paths.StopServersMarker, DateTime.UtcNow.AddDays(-1)); // before this process started

        await Host(hosted: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(_configs.Paths.StopServersMarker));
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

    [Fact]
    public async Task ABackupOfAStoppedServerReadsItsDirectoryAsItIs()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var snapshots = new CopySnapshots();
        var host = Host(snapshots: snapshots);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var made = await BackupAsync(host, serverId);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Stopped), made.Capture);
        Assert.Equal(0, snapshots.Taken);
    }

    [Fact]
    public async Task ARunningGameIsAskedToSaveThenItsFilesAreReadFromASnapshot()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var snapshots = new CopySnapshots();
        var host = Host(hosted: true, snapshots: snapshots);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        game.OnInput = text =>
        {
            if (text == "save-all\r\n")
            {
                game.Print("Saved the game\n");
            }
        };
        snapshots.OnTaken = () =>
        {
            // The game was saved, and stays paused until the snapshot is taken (a next run of the agent would resume it).
            Assert.Equal(["save-off\r\n", "save-all\r\n"], game.Input);
            Assert.Single(_store.LoadPausedSaves());
        };

        var made = await BackupAsync(host, serverId, out var location);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Snapshot, null, GameSaveOutcome.Confirmed), made.Capture);
        Assert.Equal(["save-off\r\n", "save-all\r\n", "save-on\r\n"], game.Input);
        Assert.Contains("world/snapshot.txt", Entries(location.Archive)); // read from the snapshot, not the live directory
        Assert.Equal(1, snapshots.Deleted);
        Assert.Empty(_store.LoadPausedSaves());
        Assert.Contains("Asking the game to save before a backup", Text(host, serverId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASnapshotARunningGameIsReadLiveAndSavesAgainAfterTheCopy()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var host = Host(hosted: true, snapshots: new NoVolumeSnapshots(BackupSnapshotErrors.NoSpace));
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        game.OnInput = text =>
        {
            if (text == "save-all\r\n")
            {
                game.Print("Saved the game\n");
            }
        };

        var made = await BackupAsync(host, serverId);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Live, BackupSnapshotErrors.NoSpace, GameSaveOutcome.Confirmed), made.Capture);
        Assert.Equal(["save-off\r\n", "save-all\r\n", "save-on\r\n"], game.Input);
        Assert.Empty(_store.LoadPausedSaves());
    }

    [Fact]
    public async Task AGameThatDoesNotConfirmItsSaveIsBackedUpAnywayAndSavesAgain()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest.Replace("\"timeoutSeconds\": 30", "\"timeoutSeconds\": 1", StringComparison.Ordinal));
        WriteServerFile("world/level.dat", "the world");
        var host = Host(hosted: true, snapshots: new CopySnapshots());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);

        var made = await BackupAsync(host, serverId);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Snapshot, null, GameSaveOutcome.Unconfirmed), made.Capture);
        Assert.Equal("save-on\r\n", game.Input[^1]);
    }

    [Fact]
    public async Task ASnapshotThatBreaksUnexpectedlyStillLeavesALiveBackup()
    {
        var serverId = Stored("abcd2345");
        WriteServerFile("bin/game.exe", "the game");
        var host = Host(hosted: true, snapshots: new BrokenSnapshots());
        await host.StartAsync(TestContext.Current.CancellationToken);
        await RunningAsync(host, serverId);

        var made = await BackupAsync(host, serverId);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Live, BackupSnapshotErrors.Failed), made.Capture);
        Assert.Equal(1, made.FileCount);
    }

    [Fact]
    public async Task SnapshotsTurnedOffReadTheRunningGameLive()
    {
        var serverId = Stored("abcd2345");
        WriteServerFile("bin/game.exe", "the game");
        var snapshots = new CopySnapshots();
        var host = Host(hosted: true, new ServerHostOptions { ConsoleLog = null, BackupSnapshots = false }, snapshots);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await RunningAsync(host, serverId);

        var made = await BackupAsync(host, serverId);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Live, BackupSnapshotErrors.Disabled), made.Capture); // no save in its manifest
        Assert.Equal(0, snapshots.Taken);
    }

    [Fact]
    public async Task AGameABackupLeftNotSavingIsResumedByTheAgentsNextRun()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        var host = Host(hosted: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        // The agent stopped between "save-off" and "save-on" (a crash, an update): the game would never save again.
        _store.SavePausedSave(new PausedSave(serverId, ["save-on"]));
        await RestartAgentAsync();

        await Host(hosted: true).StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["save-on\r\n"], game.Input);
        Assert.Empty(_store.LoadPausedSaves());
    }

    [Fact]
    public async Task AGameWithoutASaveConfirmationIsGivenItsWaitBeforeTheCopy()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest.Replace("\"confirmation\": \"^Saved the game\", \"timeoutSeconds\": 30",
            "\"waitSeconds\": 1", StringComparison.Ordinal));
        WriteServerFile("world/level.dat", "the world");
        var host = Host(hosted: true, snapshots: new CopySnapshots());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        var waited = System.Diagnostics.Stopwatch.StartNew();

        var made = await BackupAsync(host, serverId);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Snapshot, null, GameSaveOutcome.Waited), made.Capture);
        Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(0.9), $"{waited.Elapsed}");
        Assert.Equal(["save-off\r\n", "save-all\r\n", "save-on\r\n"], game.Input);
    }

    [Fact]
    public async Task ABackupThatFailsWhileTheGameIsPausedStillLetsItSaveAgain()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var host = Host(hosted: true, snapshots: new NoVolumeSnapshots());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        game.OnInput = text =>
        {
            if (text == "save-all\r\n")
            {
                game.Print("Saved the game\n");
            }
        };
        IReadOnlyList<string>? whileReading = null;
        _backupFiles.OnRead = _ =>
        {
            whileReading = game.Input;
            throw new IOException("The disk went away.");
        };

        var result = await BackupResultAsync(host, serverId);

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(["save-off\r\n", "save-all\r\n"], whileReading); // read live, so still paused
        Assert.Equal(["save-off\r\n", "save-all\r\n", "save-on\r\n"], game.Input);
        Assert.Empty(_store.LoadPausedSaves());
    }

    [Fact]
    public async Task ABackupThatFailsWhileReadingItsSnapshotStillDeletesIt()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var snapshots = new CopySnapshots();
        var host = Host(hosted: true, snapshots: snapshots);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        game.OnInput = text =>
        {
            if (text == "save-all\r\n")
            {
                game.Print("Saved the game\n");
            }
        };
        _backupFiles.OnRead = _ => throw new IOException("The snapshot went away.");

        var result = await BackupResultAsync(host, serverId);

        Assert.Equal(OperationOutcome.Failed, result.Outcome);
        Assert.Equal(1, snapshots.Deleted);
        Assert.Equal(["save-off\r\n", "save-all\r\n", "save-on\r\n"], game.Input); // resumed once, when the snapshot was taken
        Assert.Empty(_store.LoadPausedSaves());
    }

    [Fact]
    public async Task ABackupCancelledWhileItsSnapshotIsTakenStillLetsTheGameSaveAgain()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        var snapshots = new PendingSnapshots();
        var host = Host(hosted: true, snapshots: snapshots);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        game.OnInput = text =>
        {
            if (text == "save-all\r\n")
            {
                game.Print("Saved the game\n");
            }
        };
        var operation = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new CreateBackupCommand(serverId, Guid.NewGuid()), operation))).Status);
        await snapshots.Taking.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(_store.LoadPausedSaves());

        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new CancelOperationCommand(operation), null))).Status);

        Assert.Equal(OperationOutcome.Cancelled, (await ResultAsync(operation)).Outcome);
        Assert.Equal(["save-off\r\n", "save-all\r\n", "save-on\r\n"], game.Input);
        Assert.Empty(_store.LoadPausedSaves());
    }

    [Fact]
    public async Task AnAgentStoppingDuringABackupLetsTheGameSaveAgainBeforeLeavingItRunning()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var host = Host(hosted: true, snapshots: new CopySnapshots());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        var operation = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new CreateBackupCommand(serverId, Guid.NewGuid()), operation))).Status);
        await WaitUntilAsync(() => game.Input.Contains("save-all\r\n")); // the game never confirms: the backup waits for it

        await RestartAgentAsync();

        Assert.Equal(["save-off\r\n", "save-all\r\n", "save-on\r\n"], game.Input);
        Assert.Empty(_store.LoadPausedSaves());
        Assert.False(game.Disposed);
        var result = await ResultAsync(operation);
        Assert.Equal((OperationOutcome.Cancelled, "The agent stopped."), (result.Outcome, result.Message));
    }

    [Fact]
    public async Task AnOperationTheAgentsStopEndsIsInTheOutboxBeforeTheStopReturns()
    {
        var serverId = Stored("abcd2346", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var host = Host(hosted: true, snapshots: new CopySnapshots());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        var operation = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new CreateBackupCommand(serverId, Guid.NewGuid()), operation))).Status);
        await WaitUntilAsync(() => game.Input.Contains("save-all\r\n"));
        // Writing the result takes a while: another program holds its file for half a second.
        var held = new FileStream(Path.Combine(_configs.Paths.OutboxDirectory, $"{operation:N}.json"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None);
        var released = Task.Delay(500, TestContext.Current.CancellationToken).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

        await RestartAgentAsync();

        // An agent whose process ends right after its stop must have reported the operation.
        Assert.True(released.IsCompleted, "The stop returned before the operation's result was written.");
        await released;
        Assert.Equal(OperationOutcome.Cancelled, (await ResultAsync(operation)).Outcome);
    }

    [Fact]
    public async Task AGameThatCouldNotBeToldToResumeKeepsItsPausedSaveForTheNextRun()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        var host = Host(hosted: true, snapshots: new CopySnapshots());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var game = await RunningAsync(host, serverId);
        game.IgnoreQuit = true;
        var operation = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new CreateBackupCommand(serverId, Guid.NewGuid()), operation))).Status);
        await WaitUntilAsync(() => game.Input.Contains("save-all\r\n"));
        // Stopping: the game takes no more commands, yet it still runs (and may not save until it ends).
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StopServerCommand(serverId), null))).Status);
        await WaitForAsync(host, serverId, RuntimeState.Stopping);

        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new CancelOperationCommand(operation), null))).Status);

        Assert.Equal(OperationOutcome.Cancelled, (await ResultAsync(operation)).Outcome);
        Assert.DoesNotContain("save-on\r\n", game.Input);
        Assert.Equal(serverId, Assert.Single(_store.LoadPausedSaves()).ServerId);
        game.Exit(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServerThatStartsWhileItsFilesAreReadIsNotCalledStopped(bool stoppedAgain)
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        var host = Host();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var started = false;
        _backupFiles.OnRead = path =>
        {
            if (started)
            {
                return;
            }

            // A backup is not exclusive: the server may start while its files are read.
            started = true;
            _ = host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null));
            Assert.True(SpinWait.SpinUntil(() => State(host, serverId) == RuntimeState.Starting, TimeSpan.FromSeconds(10)));
            if (stoppedAgain)
            {
                _ = host.ExecuteAsync(Envelope(new KillServerCommand(serverId), null));
                Assert.True(SpinWait.SpinUntil(() => State(host, serverId) is RuntimeState.Offline or RuntimeState.Crashed, TimeSpan.FromSeconds(10)));
            }
        };

        var made = await BackupAsync(host, serverId);

        Assert.Equal(new BackupCapture(BackupCaptureMode.Live, BackupSnapshotErrors.GameStarted), made.Capture);
    }

    [Fact]
    public async Task ACleanRestoreDeletesTheFilesMadeAfterTheBackup()
    {
        var serverId = Stored("abcd2345", manifest: SavingManifest);
        WriteServerFile("world/level.dat", "the world");
        WriteServerFile("bin/game.exe", "the game");
        var host = Host();
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Contains(AgentCapabilities.CleanRestore, host.Capabilities);
        var backupId = Guid.NewGuid();
        var made = await BackupAsync(host, serverId, backupId);
        WriteServerFile("world/region/r.1.1.mca", "explored later");
        WriteServerFile("bin/update.dll", "outside the backup");

        var restore = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new RestoreBackupCommand(serverId, backupId, made.Sha256, Clean: true),
            restore))).Status);

        Assert.Equal(OperationOutcome.Succeeded, (await ResultAsync(restore)).Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "servers", "abcd2345", "world", "region", "r.1.1.mca")));
        Assert.True(File.Exists(Path.Combine(_root, "servers", "abcd2345", "bin", "update.dll")));
    }

    private const string SavingManifest = """
        {
          "schemaVersion": 1, "key": "pulsar-test", "version": "1.0.0", "name": "Pulsar test server", "platforms": ["windows-x64"],
          "runtime": { "windows-x64": { "executable": "bin/game.exe", "console": { "mode": "pty" } } },
          "readiness": { "type": "logPattern", "pattern": "^ready", "timeoutSeconds": 60 },
          "stop": { "steps": [ { "type": "consoleCommand", "command": "quit" } ], "timeoutSeconds": 15 },
          "backup": {
            "include": ["world/**"],
            "save": { "commands": ["save-off", "save-all"], "confirmation": "^Saved the game", "timeoutSeconds": 30, "resume": ["save-on"] }
          }
        }
        """;

    private void WriteServerFile(string path, string content)
    {
        var file = Path.Combine(_root, "servers", "abcd2345", path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private async Task<FakeGameProcess> RunningAsync(ServerHost host, Guid serverId)
    {
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new StartServerCommand(serverId), null))).Status);
        var game = await StartedAsync();
        game.Print("ready\n");
        await WaitForAsync(host, serverId, RuntimeState.Running);
        return game;
    }

    private Task<BackupResult> BackupAsync(ServerHost host, Guid serverId) => BackupAsync(host, serverId, Guid.NewGuid());

    private Task<BackupResult> BackupAsync(ServerHost host, Guid serverId, out BackupLocation location)
    {
        var backupId = Guid.NewGuid();
        location = new BackupLocation(Path.Combine(_root, "backups"), "abcd2345", backupId);
        return BackupAsync(host, serverId, backupId);
    }

    private async Task<BackupResult> BackupAsync(ServerHost host, Guid serverId, Guid backupId)
    {
        var result = await BackupResultAsync(host, serverId, backupId);
        Assert.True(result.Outcome == OperationOutcome.Succeeded, result.Message);
        return Assert.IsType<BackupResult>(result.Result);
    }

    private async Task<OperationCompleted> BackupResultAsync(ServerHost host, Guid serverId, Guid? backupId = null)
    {
        var operation = Guid.NewGuid();
        Assert.Equal(AckStatus.Accepted, (await host.ExecuteAsync(Envelope(new CreateBackupCommand(serverId, backupId ?? Guid.NewGuid()), operation))).Status);
        return await ResultAsync(operation);
    }

    private static RuntimeState State(ServerHost host, Guid serverId) => host.Snapshots().Single(s => s.ServerId == serverId).State;

    private static List<string> Entries(string archive)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    private Guid Stored(string shortId, string manifest = Manifest, bool autoStart = false)
    {
        var serverId = Guid.NewGuid();
        Directory.CreateDirectory(_configs.Paths.OutboxDirectory);
        _store.Save(new ServerRecord(serverId, new ServerRuntimeSpec(shortId, manifest, new Dictionary<string, string?>(), [], AutoStart: autoStart,
            RestartOnCrash: false), DesiredRunning: false, ResumeOnStart: false));
        return serverId;
    }

    /// <summary>The agent stops (its games keep running in their hosts unless asked otherwise) and starts again in a new host.</summary>
    private async Task RestartAgentAsync()
    {
        await _host!.StopAsync(CancellationToken.None);
        _host.Dispose();
        _host = null;
        _fileCommands?.Dispose();
        _steamCmd?.Dispose();
    }

    private async Task<FakeGameProcess> StartedAsync()
    {
        await WaitUntilAsync(() => _games.Started.Count > 0);
        return _games.Last;
    }

    private static string Text(ServerHost host, Guid serverId) =>
        string.Concat(host.ConsoleOf(serverId)!.Read(null, int.MaxValue).Select(e => e.Text));

    private static Task WaitForAsync(ServerHost host, Guid serverId, RuntimeState state) =>
        WaitUntilAsync(() => host.Snapshots().Single(s => s.ServerId == serverId).State == state);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private ServerHost Host(bool hosted = false, ServerHostOptions? options = null, IVolumeSnapshots? snapshots = null)
    {
        var channel = new PanelChannel();
        var files = new DirectServerFiles();
        var processes = hosted ? _games : new FakeProcessManager();
        GameHostManager? hosts = null;
        if (hosted)
        {
            _launcher ??= new InProcessHostLauncher(_games);
            hosts = new GameHostManager(_games, _launcher, NullLogger<GameHostManager>.Instance);
        }

        var install = new InstallOptions();
        _steamCmd = new SteamCmdRunner(processes, new NoSteamCmd(), files, new DirectServerFileManager(), new SteamLoginStore(_configs.Paths, new PlainStateProtector()), new SteamCmdOptions(), TimeProvider.System, NullLogger<SteamCmdRunner>.Instance);
        _fileCommands = new FileCommands(new DirectServerFileManager(), channel, new FileCommandOptions(), NullLogger<FileCommands>.Instance);
        _host = new ServerHost(_configs, _store, _provisioner, processes, new InstallSteps(install, files), _steamCmd, files, new NoRcon(),
            new UnmanagedFirewall(), _fileCommands, new BackupStore(_backupFiles, NullLogger<BackupStore>.Instance), install,
            channel, new SupervisorOptions(), options ?? new ServerHostOptions { ConsoleLog = null }, TimeProvider.System, NullLoggerFactory.Instance,
            hosts: hosts, snapshots: snapshots);
        return _host;
    }

    private sealed class BrokenSnapshots : IVolumeSnapshots
    {
        public Task<IVolumeSnapshot> TakeAsync(string directory, CancellationToken cancellationToken) =>
            throw new InvalidCastException("A COM interface that is not there.");
    }

    /// <summary>Snapshots that take until the backup is cancelled.</summary>
    private sealed class PendingSnapshots : IVolumeSnapshots
    {
        public TaskCompletionSource Taking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IVolumeSnapshot> TakeAsync(string directory, CancellationToken cancellationToken)
        {
            Taking.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Never taken.");
        }
    }

    /// <summary>"Snapshots" that copy the directory, with a file of their own to show the backup read the copy.</summary>
    private sealed class CopySnapshots : IVolumeSnapshots
    {
        public int Taken { get; private set; }

        public int Deleted { get; private set; }

        public Action? OnTaken { get; set; }

        public Task<IVolumeSnapshot> TakeAsync(string directory, CancellationToken cancellationToken)
        {
            Taken++;
            OnTaken?.Invoke();
            var copy = directory + ".snapshot";
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(directory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            File.WriteAllText(Path.Combine(copy, "world", "snapshot.txt"), "only in the snapshot");
            return Task.FromResult<IVolumeSnapshot>(new Copy(copy, this));
        }

        private sealed class Copy(string directory, CopySnapshots owner) : IVolumeSnapshot
        {
            public string Directory { get; } = directory;

            public ValueTask DisposeAsync()
            {
                owner.Deleted++;
                System.IO.Directory.Delete(Directory, recursive: true);
                return ValueTask.CompletedTask;
            }
        }
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
