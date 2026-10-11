using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// Games in host processes (docs/architecture/06, "Server hosts"), with the hosts running in this test process over
/// real named pipes: what the agent asks reaches the game, the game outlives the agent's connection, and an agent that
/// comes back finds it with the output it missed.
/// </summary>
public sealed class GameHostTests : IAsyncDisposable
{
    private readonly FakeProcessManager _games = new();
    private readonly InProcessHostLauncher _launcher;
    private readonly GameHostManager _hosts;

    public GameHostTests()
    {
        _launcher = new InProcessHostLauncher(_games);
        _hosts = new GameHostManager(_games, _launcher, NullLogger<GameHostManager>.Instance);
    }

    public ValueTask DisposeAsync() => _launcher.DisposeAsync();

    [Fact]
    public async Task AGameStartsInItsHostAndAnswersThroughThePipe()
    {
        var process = await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        Assert.Equal(game.ProcessId, process.ProcessId);
        Assert.Equal(Spec().Arguments, Assert.Single(_games.Specs).Arguments);

        game.Print("hello\r\n");
        var chunk = await NextAsync(process);
        Assert.Equal(("hello\r\n", 7L), (chunk.Text, chunk.End));

        await process.WriteInputAsync("status\r\n", TestContext.Current.CancellationToken);
        Assert.Equal("status\r\n", Assert.Single(game.Input));

        game.Usage = new ProcessUsage(TimeSpan.FromSeconds(3), 1234, 2, 99);
        Assert.Equal(game.Usage, process.SampleUsage());
        Assert.Equal(2, process.CountProcesses());

        process.ApplyLimits(new ProcessLimits(50, null));
        Assert.Equal(new ProcessLimits(50, null), Assert.Single(game.AppliedLimits));

        process.TerminateTree(7);
        Assert.Equal(7, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var host = ((IHostedGameProcess)process).Host;
        await process.DisposeAsync();
        await _launcher.WaitEndedAsync(host);
        Assert.True(game.Disposed);
    }

    [Fact]
    public async Task ADetachedGameKeepsRunningAndTheNextAgentGetsTheOutputItMissed()
    {
        var process = (IHostedGameProcess)await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        game.Print("before\n");
        var seen = (await NextAsync(process)).End;

        await process.DetachAsync();
        game.Print("while away\n");
        Assert.False(game.Disposed);
        Assert.True(_launcher.IsAlive(process.Host));

        var again = await _hosts.ReattachAsync(process.Host, seen, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        Assert.Equal(game.ProcessId, again.ProcessId);
        Assert.Equal(game.StartedAt, again.StartedAt);
        var chunk = await NextAsync(again);
        Assert.Equal(("while away\n", seen + 11), (chunk.Text, chunk.End));

        await again.WriteInputAsync("say hi\n", TestContext.Current.CancellationToken);
        Assert.Equal("say hi\n", Assert.Single(game.Input));
        Assert.False(again.Exited.IsCompleted);
        await again.DisposeAsync();
    }

    [Fact]
    public async Task AGameThatExitedWhileNoAgentWasThereIsReportedWhenOneComes()
    {
        var process = (IHostedGameProcess)await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        await process.DetachAsync();
        game.ReachedMemoryLimit = true;
        game.Exit(3);

        var again = await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        Assert.Equal(3, await again.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(again.ReachedMemoryLimit);
        await again.DisposeAsync();
        await _launcher.WaitEndedAsync(process.Host);
    }

    [Fact]
    public async Task OutputTheHostCouldNotKeepIsReportedAsLost()
    {
        await using var launcher = new InProcessHostLauncher(_games, new GameHostOptions { OutputCapacity = 1024 });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance);
        var process = (IHostedGameProcess)await hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        await process.DetachAsync();
        for (var i = 0; i < 30; i++)
        {
            game.Print(new string((char)('a' + (i % 26)), 100));
        }

        await WaitUntilAsync(() => InProcessHostLauncher.Status(process.Host)?.OutputEnd == 3000);
        var again = await hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        var note = await NextAsync(again);
        Assert.Contains("2000 characters of output were lost", note.Text, StringComparison.Ordinal);
        Assert.Equal(2000, note.End);

        var rest = new StringBuilder();
        long end = 0;
        while (end < 3000)
        {
            var chunk = await NextAsync(again);
            rest.Append(chunk.Text);
            end = chunk.End;
        }

        Assert.Equal(1000, rest.Length);
        Assert.StartsWith(new string('u', 100), rest.ToString(), StringComparison.Ordinal);
        await again.DisposeAsync();
    }

    [Fact]
    public async Task AHostThatDiesTakesItsGameWithItAndTheAgentSeesAnExit()
    {
        var process = (IHostedGameProcess)await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;

        _launcher.Kill(process.Host);

        Assert.Equal(HostedGameProcess.HostLostExitCode, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await _launcher.WaitEndedAsync(process.Host);
        Assert.True(game.Disposed);
        Assert.Null(await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken));
        await process.DisposeAsync();
    }

    [Fact]
    public async Task AGameThatCannotStartFailsWithItsCodeAndLeavesNoHost()
    {
        _games.FailNext = new GameProcessException("process.invalid_executable", "Only .exe files can be started.");

        var error = await Assert.ThrowsAsync<GameProcessException>(() => _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken));

        Assert.Equal("process.invalid_executable", error.Code);
        await WaitUntilAsync(() => _launcher.Launched.All(h => !_launcher.IsAlive(h)));
    }

    [Fact]
    public async Task AHostRefusesAnAgentThatSpeaksAnotherProtocol()
    {
        var pipe = GameHostProtocol.NewPipeName(Guid.NewGuid());
        var host = _launcher.Launch(Guid.NewGuid(), pipe);
        await using var client = GameHostPipes.CreateClient(pipe);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);

        await GameHostProtocol.WriteAsync(client, new HostHello(1, GameHostProtocol.Version + 1), TestContext.Current.CancellationToken);
        var reply = Assert.IsType<HostReply>(await GameHostProtocol.ReadAsync(client, TestContext.Current.CancellationToken));
        Assert.Equal("host.protocol", reply.ErrorCode);

        await GameHostProtocol.WriteAsync(client, new HostSubscribe(2, 0), TestContext.Current.CancellationToken);
        reply = Assert.IsType<HostReply>(await GameHostProtocol.ReadAsync(client, TestContext.Current.CancellationToken));
        Assert.Equal("host.protocol", reply.ErrorCode);

        // A frame larger than the protocol allows ends the connection, not the host.
        await client.WriteAsync(BitConverter.GetBytes(GameHostProtocol.MaxFrameBytes + 1), TestContext.Current.CancellationToken);
        Assert.Null(await GameHostProtocol.ReadAsync(client, TestContext.Current.CancellationToken));
        Assert.True(_launcher.IsAlive(host));
    }

    [Fact]
    public async Task FramesTheHostCannotReadEndTheConnectionNotTheHost()
    {
        var process = (IHostedGameProcess)await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        await process.DetachAsync();

        // No kind (System.Text.Json fails on the abstract type with NotSupportedException), then a kind nobody knows.
        foreach (var frame in new[] { """{"id":1}""", """{"kind":"nonsense","id":1}""" })
        {
            await using var client = await ConnectAsync(process.Host);
            await WriteRawAsync(client, frame);
            Assert.Null(await GameHostProtocol.ReadAsync(client, TestContext.Current.CancellationToken));
            Assert.True(_launcher.IsAlive(process.Host));
        }

        // A request whose handling fails (input without text) is answered as failed.
        await using (var client = await ConnectAsync(process.Host))
        {
            await GameHostProtocol.WriteAsync(client, new HostHello(1, GameHostProtocol.Version), TestContext.Current.CancellationToken);
            Assert.Null(Assert.IsType<HostReply>(await GameHostProtocol.ReadAsync(client, TestContext.Current.CancellationToken)).ErrorCode);
            await WriteRawAsync(client, """{"kind":"input","id":2,"text":null}""");
            var reply = Assert.IsType<HostReply>(await GameHostProtocol.ReadAsync(client, TestContext.Current.CancellationToken));
            Assert.Equal((2, "host.request_failed"), (reply.Id, reply.ErrorCode));
        }

        Assert.True(_launcher.IsAlive(process.Host));
        Assert.False(game.Disposed);
        var again = await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        await again.DisposeAsync();
        Assert.Equal(0, await _launcher.RunOf(process.Host).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AFrameWithoutAKindIsNotAFrame()
    {
        using var stream = new MemoryStream();
        var json = """{"id":1}"""u8.ToArray();
        stream.Write(BitConverter.GetBytes(json.Length));
        stream.Write(json);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => GameHostProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task TheLastOutputOfAGameReachesTheAgentWhenItsHostIsReleased()
    {
        // A pseudo console shows its final screen as it closes, after the game's exit was reported.
        _games.OnStarted = (game, _) => game.LastScreen = "Server shut down.\r\n";
        var process = await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        _games.Last.Exit(0);
        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await process.DisposeAsync();

        var text = new StringBuilder();
        await foreach (var chunk in process.Output.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            text.Append(chunk.Text);
        }

        Assert.Equal("Server shut down.\r\n", text.ToString());
    }

    [Fact]
    public async Task ReleasingAHostEndsTheChildrenItsGameLeft()
    {
        var process = await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        game.Children = 2;

        await process.DisposeAsync();

        Assert.Equal(1, game.TerminatedWith);
        Assert.True(game.Disposed);
        await _launcher.WaitEndedAsync(((IHostedGameProcess)process).Host);
    }

    [Fact]
    public async Task AHostWithNobodyToDecideEndsTheChildrenOfAnExitedGameAfterTheGrace()
    {
        await using var launcher = new InProcessHostLauncher(_games, new GameHostOptions { TreeGrace = TimeSpan.FromMilliseconds(100) });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance);
        var process = (IHostedGameProcess)await hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        await process.DetachAsync();

        game.Children = 1; // a crash handler, say
        game.Exit(3);

        await WaitUntilAsync(() => game.TerminatedWith == 1);
        Assert.True(launcher.IsAlive(process.Host)); // it keeps the exit code for the next agent
        Assert.Equal(3, InProcessHostLauncher.Status(process.Host)?.ExitCode);
    }

    [Fact]
    public async Task AHostNobodyConnectsToEnds()
    {
        using var service = new GameHostService(_games, Guid.NewGuid(), GameHostProtocol.NewPipeName(Guid.NewGuid()),
            new GameHostOptions { FirstConnectionTimeout = TimeSpan.FromMilliseconds(200) }, TimeProvider.System, NullLogger<GameHostService>.Instance);

        Assert.Equal(0, await service.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Empty(_games.Started);
    }

    [Fact]
    public async Task AHostThatIsNotTheProcessServingThePipeIsKilledRatherThanTrusted()
    {
        var process = (IHostedGameProcess)await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        await process.DetachAsync();
        _launcher.Trusted = false;

        Assert.Null(await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken));

        await _launcher.WaitEndedAsync(process.Host);
        Assert.True(game.Disposed);
    }

    [Fact]
    public async Task AClientThatConnectsAndLeavesAtOnceDoesNotEndTheHost()
    {
        // Seen under load: the pipe broke before the host served it, and the next wait for a connection failed.
        var process = (IHostedGameProcess)await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        await process.DetachAsync();

        for (var i = 0; i < 3; i++)
        {
            await using var gone = new System.IO.Pipes.NamedPipeClientStream(".", process.Host.Pipe, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            await gone.ConnectAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        var again = await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        Assert.False(game.Disposed);
        await again.DisposeAsync();
    }

    [Fact]
    public async Task AHostThatAcceptsTheConnectionButNeverAnswersIsKilled()
    {
        await using var launcher = new ScriptedHostLauncher(_ => null);
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance) { ReattachTimeout = TimeSpan.FromMilliseconds(300) };
        var host = launcher.Launch(Guid.NewGuid(), GameHostProtocol.NewPipeName(Guid.NewGuid()));

        Assert.Null(await hosts.ReattachAsync(host, 0, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(launcher.Killed);
    }

    [Fact]
    public async Task AHostOfAnotherProtocolVersionIsLeftRunning()
    {
        // An update of the agent that changed the protocol: killing the host would end its game.
        await using var launcher = new InProcessHostLauncher(_games, new GameHostOptions { Protocol = GameHostProtocol.Version + 1 });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance);
        var host = launcher.Launch(Guid.NewGuid(), GameHostProtocol.NewPipeName(Guid.NewGuid()));

        var error = await Assert.ThrowsAsync<GameProcessException>(() => hosts.ReattachAsync(host, 0, TestContext.Current.CancellationToken));

        Assert.Equal("host.incompatible", error.Code);
        Assert.True(launcher.IsAlive(host));
    }

    [Fact]
    public async Task AHostThatDoesNotAnswerItsReleaseIsKilled()
    {
        await using var launcher = new ScriptedHostLauncher(request => request switch
        {
            HostHello hello => new HostReply(hello.Id, Status: new GameHostStatus(Guid.NewGuid(), 4242, DateTimeOffset.UtcNow, null, false, true, 0, 0)),
            HostSubscribe subscribe => new HostReply(subscribe.Id),
            _ => null, // stuck
        });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance) { RequestTimeout = TimeSpan.FromMilliseconds(300) };
        var host = launcher.Launch(Guid.NewGuid(), GameHostProtocol.NewPipeName(Guid.NewGuid()));
        var process = await hosts.ReattachAsync(host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(process);

        await process.DisposeAsync();

        Assert.True(launcher.Killed);
    }

    [Fact]
    public async Task AGameThatDoesNotTakeItsInputCanStillBeKilledAndReleased()
    {
        // A frozen game: its host's write of a console command never ends, and must not keep the host from the rest.
        var process = (IHostedGameProcess)await _hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        var gate = game.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                wait.CancelAfter(TimeSpan.FromMilliseconds(300));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WriteInputAsync("say hi\n", wait.Token).AsTask());
            }

            process.TerminateTree(7);

            Assert.Equal(7, await process.Exited.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await process.DisposeAsync();
            await _launcher.WaitEndedAsync(process.Host);
            Assert.True(game.Disposed);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task InputAHostDoesNotTakeInTimeFails()
    {
        await using var launcher = new ScriptedHostLauncher(request => request switch
        {
            HostHello hello => new HostReply(hello.Id, Status: new GameHostStatus(Guid.NewGuid(), 4242, DateTimeOffset.UtcNow, null, false, true, 0, 0)),
            HostSubscribe subscribe => new HostReply(subscribe.Id),
            _ => null, // stuck
        });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance) { RequestTimeout = TimeSpan.FromMilliseconds(300) };
        var host = launcher.Launch(Guid.NewGuid(), GameHostProtocol.NewPipeName(Guid.NewGuid()));
        var process = await hosts.ReattachAsync(host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(process);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            process.WriteInputAsync("say hi\n", CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await process.DisposeAsync();
    }

    [Fact]
    public async Task AHostThatDoesNotAnswerAKillIsKilledWithItsGame()
    {
        await using var launcher = new ScriptedHostLauncher(request => request switch
        {
            HostHello hello => new HostReply(hello.Id, Status: new GameHostStatus(Guid.NewGuid(), 4242, DateTimeOffset.UtcNow, null, false, true, 0, 0)),
            HostSubscribe subscribe => new HostReply(subscribe.Id),
            _ => null, // stuck
        });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance) { RequestTimeout = TimeSpan.FromMilliseconds(300) };
        var host = launcher.Launch(Guid.NewGuid(), GameHostProtocol.NewPipeName(Guid.NewGuid()));
        var process = await hosts.ReattachAsync(host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(process);

        process.TerminateTree(1);

        Assert.True(launcher.Killed);
        Assert.Equal(HostedGameProcess.HostLostExitCode, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await process.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the write that reached the game's input cannot be taken back
    public async Task ConsoleInputTheAgentGaveUpOnIsNeverWrittenLate(bool writeIgnoresCancellation)
    {
        // A game that froze: the user's command was refused (console.not_reading) and typed again. When the game reads
        // again, the copies the host still held must not run then.
        await using var launcher = new InProcessHostLauncher(_games, new GameHostOptions { InputTimeout = TimeSpan.FromMilliseconds(300) });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance);
        var process = await hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        var gate = game.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        game.InputIgnoresCancellation = writeIgnoresCancellation;
        try
        {
            foreach (var command in new[] { "kick 1\n", "kick 2\n" })
            {
                using var agentWait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                agentWait.CancelAfter(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WriteInputAsync(command, agentWait.Token).AsTask());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken); // past both commands' time in the host
            gate.SetResult();
            await process.WriteInputAsync("status\n", TestContext.Current.CancellationToken);

            Assert.Equal(writeIgnoresCancellation ? ["kick 1\n", "status\n"] : ["status\n"], game.Input);
        }
        finally
        {
            gate.TrySetResult();
            await process.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConsoleInputPilingUpInTheHostIsRefusedAtOnce()
    {
        await using var launcher = new InProcessHostLauncher(_games, new GameHostOptions { InputTimeout = TimeSpan.FromSeconds(30) });
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance);
        var process = await hosts.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var game = _games.Last;
        var gate = game.InputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        game.InputIgnoresCancellation = true;
        try
        {
            var writes = Enumerable.Range(0, 20).Select(i => process.WriteInputAsync($"say {i}\n", CancellationToken.None).AsTask()).ToList();

            // One being written and 15 behind it; the rest are refused without waiting.
            await WaitUntilAsync(() => writes.Count(w => w.IsCompleted) == 4);
            foreach (var refused in writes.Where(w => w.IsCompleted))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => refused);
                Assert.Equal(GameHostErrors.InputBusy, Assert.IsType<GameProcessException>(error.InnerException).Code);
            }

            var waiting = writes.Where(w => !w.IsCompleted).ToList();
            gate.SetResult();
            await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(16, game.Input.Count);
        }
        finally
        {
            gate.TrySetResult();
            await process.DisposeAsync();
        }
    }

    [Fact]
    public async Task AKillOfAHostThatDoesNotAnswerIsAcknowledgedWellWithinThePanelsWait()
    {
        // The panel waits 10 s for a kill's ack: a host that does not kill its game gets far less before it is killed itself.
        Assert.True(_hosts.TerminateTimeout <= TimeSpan.FromSeconds(3));
        await using var launcher = new ScriptedHostLauncher(StuckAfterStart);
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance)
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
            TerminateTimeout = TimeSpan.FromMilliseconds(300),
        };
        var events = new RecordingEvents();
        await using var supervisor = Supervisor(hosts, TimeProvider.System, events);
        await supervisor.StartAsync();
        await supervisor.FlushAsync(); // the game runs in its host

        Assert.Equal(AckStatus.Accepted, await supervisor.KillAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(launcher.Killed);
        await events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Killed);
    }

    [Fact]
    public async Task AStopThatTimesOutKillsAHostThatDoesNotAnswer()
    {
        await using var launcher = new ScriptedHostLauncher(StuckAfterStart);
        var hosts = new GameHostManager(_games, launcher, NullLogger<GameHostManager>.Instance)
        {
            RequestTimeout = TimeSpan.FromSeconds(30),
            TerminateTimeout = TimeSpan.FromMilliseconds(300),
        };
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var events = new RecordingEvents();
        await using var supervisor = Supervisor(hosts, clock, events);
        await supervisor.StartAsync();
        await supervisor.FlushAsync();
        Assert.Equal(AckStatus.Accepted, await supervisor.StopAsync()); // the game takes "quit" and goes on
        await supervisor.FlushAsync();

        clock.Advance(TimeSpan.FromSeconds(15)); // the manifest's stop timeout

        var offline = await events.WaitForAsync(RuntimeState.Offline, StateChangeReason.StopTimedOut);
        Assert.Equal(HostedGameProcess.HostLostExitCode, offline.ExitCode);
        Assert.True(launcher.Killed);
    }

    /// <summary>A host that starts its game and takes its input, but never answers a kill or a release.</summary>
    private static HostReply? StuckAfterStart(HostRequest request) => request switch
    {
        HostHello hello => new HostReply(hello.Id, Status: new GameHostStatus(Guid.NewGuid(), null, null, null, false, true, 0, 0)),
        HostStart start => new HostReply(start.Id, Status: new GameHostStatus(start.Launch.ServerId, 4242, DateTimeOffset.UtcNow, null, false, true, 0, 0)),
        HostSubscribe or HostInput => new HostReply(request.Id),
        _ => null,
    };

    private static ServerSupervisor Supervisor(GameHostManager hosts, TimeProvider clock, RecordingEvents events)
    {
        var plan = new LaunchPlan("bin/game.exe", "", [], new Dictionary<string, string>(), new ConsoleDefinition(ManifestConsoleMode.Pty, InputNewline: "\r\n"),
            new LogPatternReadiness("^ready", 60), new StopDefinition([new ConsoleCommandStop("quit")], 15));
        var definition = new ServerDefinition(Guid.NewGuid(), "abcd1234", @"C:\servers\abcd1234", ProcessIdentity.CurrentUser, plan, AutoStart: false,
            RestartOnCrash: false);
        return new ServerSupervisor(definition, hosts, events, clock, new SupervisorOptions(), NullLogger.Instance);
    }

    [Fact]
    public void TheRingKeepsTheNewestOutputAndReadsFromAnyOffset()
    {
        var ring = new OutputRing(1024);
        var at = DateTimeOffset.UnixEpoch;
        ring.Append(new string('a', 600), at);
        ring.Append(new string('b', 600), at);
        Assert.Equal((600L, 1200L), (ring.Start, ring.End));

        var read = ring.Read(0, 100);
        Assert.Equal((0L, 600L), read.Lost);
        var piece = Assert.Single(read.Pieces);
        Assert.Equal((600L, 100), (piece.Offset, piece.Text.Length));

        read = ring.Read(1150, 1000);
        Assert.Null(read.Lost);
        Assert.Equal((1150L, new string('b', 50)), (read.Pieces[0].Offset, read.Pieces[0].Text));
        Assert.Empty(ring.Read(1200, 10).Pieces);

        // Never half a surrogate pair at the end of a read.
        var emoji = new OutputRing(1024);
        emoji.Append("a\U0001F600", at);
        Assert.Equal("a", Assert.Single(emoji.Read(0, 2).Pieces).Text);

        // A piece larger than the ring keeps its end, at its real offset.
        var big = new OutputRing(1024);
        big.Append(new string('x', 1000) + new string('y', 1024), at);
        Assert.Equal((1000L, 2024L), (big.Start, big.End));
    }

    [Fact]
    public void OutputWrittenACharacterAtATimeDoesNotKeepAnObjectPerCharacter()
    {
        const int capacity = 1024 * 1024;
        var at = DateTimeOffset.UnixEpoch;

        // A burst: joined into few pieces.
        var burst = new OutputRing(capacity);
        for (var i = 0; i < 100_000; i++)
        {
            burst.Append("x", at);
        }

        Assert.True(burst.PieceCount <= 100, $"{burst.PieceCount} pieces");
        Assert.Equal(new string('x', 10), string.Concat(burst.Read(burst.End - 10, 100).Pieces.Select(p => p.Text)));

        // A character every second: never joined, but the pieces kept are bounded.
        var slow = new OutputRing(capacity);
        for (var i = 0; i < 300_000; i++)
        {
            slow.Append("y", at.AddSeconds(i));
        }

        Assert.True(slow.PieceCount <= capacity / OutputRing.CharactersPerPiece, $"{slow.PieceCount} pieces");
        Assert.Equal(300_000L, slow.End);
        Assert.Equal(slow.End - slow.PieceCount, slow.Start);
    }

    [Fact]
    public void HostsRunFromACopyOfTheProgramPerVersionAndUnusedCopiesGo()
    {
        var root = Directory.CreateTempSubdirectory("pulsar-host-programs-").FullName;
        try
        {
            var program = Path.Combine(root, "program");
            var copies = Path.Combine(root, "copies");
            Directory.CreateDirectory(Path.Combine(program, "runtimes"));
            File.WriteAllText(Path.Combine(program, "PulsarAgent.exe"), "version 1");
            File.WriteAllText(Path.Combine(program, "runtimes", "native.dll"), "native");

            var first = GameHostProgram.Prepare(program, "PulsarAgent.exe", copies);
            Assert.Equal("version 1", File.ReadAllText(first));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(first)!, "runtimes", "native.dll")));
            Assert.Equal(first, GameHostProgram.Prepare(program, "PulsarAgent.exe", copies)); // the same version: the same copy

            // An update: a new copy, while a host still runs from the old one.
            File.WriteAllText(Path.Combine(program, "PulsarAgent.exe"), "version 2, longer");
            var second = GameHostProgram.Prepare(program, "PulsarAgent.exe", copies);
            Assert.NotEqual(first, second);
            Directory.CreateDirectory(Path.Combine(copies, "0123456789abcdef.tmp-12345678")); // an interrupted copy

            GameHostProgram.CleanUp(copies, second, [first]);
            Assert.Equal(2, Directory.GetDirectories(copies).Length);

            GameHostProgram.CleanUp(copies, second, []); // its host ended
            Assert.Equal(Path.GetDirectoryName(second), Assert.Single(Directory.GetDirectories(copies)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AFailedCopyOfTheProgramLeavesNothingBehind()
    {
        // Tried again at every start while it fails (a full disk): each try must not leave another partial copy.
        var root = Directory.CreateTempSubdirectory("pulsar-host-programs-").FullName;
        try
        {
            var program = Path.Combine(root, "program");
            var copies = Path.Combine(root, "copies");
            Directory.CreateDirectory(Path.Combine(program, "runtimes"));
            File.WriteAllText(Path.Combine(program, "PulsarAgent.exe"), "version 1");
            File.WriteAllText(Path.Combine(program, "runtimes", "native.dll"), "native");

            using (new FileStream(Path.Combine(program, "runtimes", "native.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => GameHostProgram.Prepare(program, "PulsarAgent.exe", copies));
                Assert.ThrowsAny<IOException>(() => GameHostProgram.Prepare(program, "PulsarAgent.exe", copies));
            }

            Assert.Empty(Directory.GetDirectories(copies));
            Assert.Equal("version 1", File.ReadAllText(GameHostProgram.Prepare(program, "PulsarAgent.exe", copies)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessLaunchSpec Spec() =>
        new(Guid.Parse("01a10000-0000-7000-8000-000000000001"), @"C:\servers\abc", "game.exe", ["-port", "27015"], ".",
            new Dictionary<string, string> { ["GAME_MODE"] = "casual" }, ConsoleMode.Pty, ConsoleEncoding.Utf8, ProcessIdentity.Account("pulsar_abc"),
            new ProcessLimits(null, 512 * 1024 * 1024));

    private static async Task<NamedPipeClientStream> ConnectAsync(GameHostReference host)
    {
        var client = GameHostPipes.CreateClient(host.Pipe);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        return client;
    }

    private static async Task WriteRawAsync(Stream stream, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), TestContext.Current.CancellationToken);
        await stream.WriteAsync(bytes, TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ConsoleChunk> NextAsync(IGameProcess process)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await process.Output.ReadAsync(timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Hosts as tasks of the test process: killing one cancels it, which closes its pipe as a dying process would.</summary>
internal sealed class InProcessHostLauncher(IGameProcessManager games, GameHostOptions? options = null) : IGameHostLauncher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Host> _hosts = new();
    private int _nextId = 40000;

    public IEnumerable<GameHostReference> Launched => _hosts.Values.Select(h => h.Reference);

    /// <summary>False: every pipe looks served by another process than its host's.</summary>
    public bool Trusted { get; set; } = true;

    public GameHostReference Launch(Guid serverId, string pipe) => Launch(serverId, pipe, options ?? new GameHostOptions());

    /// <summary>A host with options of its own (one of another protocol version among current ones, say).</summary>
    public GameHostReference Launch(Guid serverId, string pipe, GameHostOptions hostOptions)
    {
        var service = new GameHostService(games, serverId, pipe, hostOptions, TimeProvider.System,
            NullLogger<GameHostService>.Instance);
        var kill = new CancellationTokenSource();
        var reference = new GameHostReference(pipe, Interlocked.Increment(ref _nextId), DateTimeOffset.UtcNow);
        _hosts[pipe] = new Host(reference, service, Task.Run(() => service.RunAsync(kill.Token)), kill);
        return reference;
    }

    public bool IsAlive(GameHostReference host) => _hosts.TryGetValue(host.Pipe, out var running) && !running.Run.IsCompleted;

    public void Kill(GameHostReference host)
    {
        if (_hosts.TryGetValue(host.Pipe, out var running))
        {
            running.Kill.Cancel();
        }
    }

    public bool Serves(PipeStream pipe, GameHostReference host) => Trusted && _hosts.ContainsKey(host.Pipe);

    /// <summary>What the host would tell an agent now (read through a connection of its own).</summary>
    public static GameHostStatus? Status(GameHostReference host)
    {
        using var client = GameHostPipes.CreateClient(host.Pipe);
        try
        {
            client.Connect(100);
        }
        catch (TimeoutException)
        {
            return null; // an agent is connected
        }

        GameHostProtocol.WriteAsync(client, new HostHello(1, GameHostProtocol.Version), CancellationToken.None).GetAwaiter().GetResult();
        return (GameHostProtocol.ReadAsync(client, CancellationToken.None).GetAwaiter().GetResult() as HostReply)?.Status;
    }

    public async Task WaitEndedAsync(GameHostReference host) =>
        await _hosts[host.Pipe].Run.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>The host's own result: it fails when the host crashed rather than ended.</summary>
    public Task<int> RunOf(GameHostReference host) => _hosts[host.Pipe].Run;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values)
        {
            await host.Kill.CancelAsync();
            try
            {
                await host.Run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // Reported by the test that left it.
            }

            host.Service.Dispose();
            host.Kill.Dispose();
        }
    }

    private sealed record Host(GameHostReference Reference, GameHostService Service, Task<int> Run, CancellationTokenSource Kill);
}

/// <summary>A host that answers each request as the test says (null: never), for hosts that misbehave.</summary>
internal sealed class ScriptedHostLauncher(Func<HostRequest, HostReply?> answer) : IGameHostLauncher, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _serving = Task.CompletedTask;

    public bool Killed { get; private set; }

    public GameHostReference Launch(Guid serverId, string pipe)
    {
        var server = GameHostPipes.CreateServer(pipe);
        _serving = ServeAsync(server);
        return new GameHostReference(pipe, 41000, DateTimeOffset.UtcNow);
    }

    public bool IsAlive(GameHostReference host) => !Killed;

    public void Kill(GameHostReference host)
    {
        Killed = true;
        _stop.Cancel();
    }

    public bool Serves(PipeStream pipe, GameHostReference host) => true;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _serving;
        _stop.Dispose();
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
                while (await GameHostProtocol.ReadAsync(server, _stop.Token) is HostRequest request)
                {
                    if (answer(request) is { } reply)
                    {
                        await GameHostProtocol.WriteAsync(server, reply, _stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
            {
                // Killed, or the agent went away.
            }
        }
    }
}
