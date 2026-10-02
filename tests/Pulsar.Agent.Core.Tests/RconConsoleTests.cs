using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The Source RCON client against a fake game, in both dialects spike 0001 measured (srcds and CS2).</summary>
public sealed class SourceRconTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatesAndRunsCommands(bool srcds)
    {
        await using var game = new FakeRconServer("secret", srcds);
        var connector = new SourceRconConnector { AnswerIdle = TimeSpan.FromMilliseconds(100) };

        await using var session = await connector.ConnectAsync(game.Endpoint("secret"), TestContext.Current.CancellationToken);
        var echo = await session.ExecuteAsync("echo hello", TestContext.Current.CancellationToken);
        var status = await session.ExecuteAsync("status", TestContext.Current.CancellationToken);

        Assert.Equal("hello\n", echo);
        Assert.Equal(new string('x', 5000) + new string('y', 3000), status); // split over two packets by the game
        Assert.Equal(["echo hello", "status"], game.Commands);
    }

    [Fact]
    public async Task AWrongPasswordIsReportedAsSuch()
    {
        await using var game = new FakeRconServer("secret", srcds: true);

        var error = await Assert.ThrowsAsync<RconException>(() => new SourceRconConnector().ConnectAsync(game.Endpoint("wrong"),
            TestContext.Current.CancellationToken));

        Assert.True(error.AuthenticationFailed);
    }

    [Fact]
    public async Task NothingListeningIsAnRconError()
    {
        var port = FreePort();

        var error = await Assert.ThrowsAsync<RconException>(() => new SourceRconConnector().ConnectAsync(
            new RconEndpoint(RconProtocol.Source, "127.0.0.1", port, "x"), TestContext.Current.CancellationToken));

        Assert.False(error.AuthenticationFailed);
    }

    [Theory]
    [InlineData("quit\nsv_cheats 1")]
    [InlineData("")]
    public async Task CommandsAreSingleLines(string command)
    {
        await using var game = new FakeRconServer("secret", srcds: false);
        await using var session = await new SourceRconConnector().ConnectAsync(game.Endpoint("secret"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAsync(command, TestContext.Current.CancellationToken));
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>A game's RCON: srcds answers authentication with an empty response first, CS2 does not.</summary>
internal sealed class FakeRconServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeRconServer(string password, bool srcds)
    {
        _listener.Start();
        _loop = AcceptAsync(password, srcds);
    }

    public List<string> Commands { get; } = [];

    public RconEndpoint Endpoint(string password) => new(RconProtocol.Source, "127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port, password);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync(string password, bool srcds)
    {
        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
        var stream = client.GetStream();
        while (!_stop.IsCancellationRequested)
        {
            var (id, type, body) = await ReceiveAsync(stream);
            if (type == 3)
            {
                if (srcds)
                {
                    await SendAsync(stream, id, 0, "");
                }

                await SendAsync(stream, body == password ? id : -1, 2, "");
                continue;
            }

            Commands.Add(body);
            switch (body)
            {
                case "status":
                    await SendAsync(stream, id, 0, new string('x', 5000));
                    await SendAsync(stream, id, 0, new string('y', 3000));
                    break;
                default:
                    await SendAsync(stream, id, 0, body.StartsWith("echo ", StringComparison.Ordinal) ? body[5..] + "\n" : "");
                    break;
            }
        }
    }

    private async Task<(int Id, int Type, string Body)> ReceiveAsync(NetworkStream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, _stop.Token);
        var packet = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(packet, _stop.Token);
        return (BinaryPrimitives.ReadInt32LittleEndian(packet), BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)),
            Encoding.UTF8.GetString(packet, 8, packet.Length - 10));
    }

    private async Task SendAsync(NetworkStream stream, int id, int type, string body)
    {
        var text = Encoding.UTF8.GetBytes(body);
        var packet = new byte[14 + text.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet.AsSpan(12));
        await stream.WriteAsync(packet, _stop.Token);
    }
}

public sealed class LogTailTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pulsar-logtail", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task FollowsAppendedTextSkippingTheByteOrderMarkAndSplitCharacters()
    {
        var path = Path.Combine(_root, "console.log");
        await using var game = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var euro = Encoding.UTF8.GetBytes("€"); // three bytes, written in two parts
        await game.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF }.Concat("price "u8.ToArray()).Concat(euro[..1]).ToArray(), TestContext.Current.CancellationToken);
        await game.FlushAsync(TestContext.Current.CancellationToken);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var text = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            await foreach (var chunk in new LogTail(new DirectServerFiles(), _root, "console.log", Encoding.UTF8, TimeSpan.FromMilliseconds(20))
                               .ReadAsync(cancel.Token))
            {
                lock (text)
                {
                    text.Append(chunk);
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await game.WriteAsync(euro[1..].Concat("\nready\n"u8.ToArray()).ToArray(), TestContext.Current.CancellationToken);
        await game.FlushAsync(TestContext.Current.CancellationToken);
        await Eventually(() => text.ToString() == "price €\nready\n", () => text.ToString());

        // Truncated (a game that starts its log again): read from the start.
        game.SetLength(0);
        await game.WriteAsync("again\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await game.FlushAsync(TestContext.Current.CancellationToken);
        await Eventually(() => text.ToString().EndsWith("again\n", StringComparison.Ordinal), () => text.ToString());

        await cancel.CancelAsync();
        await reading;
    }

    [Fact]
    public async Task WaitsForTheFileToAppear()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = new TaskCompletionSource<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var chunk in new LogTail(new DirectServerFiles(), _root, "cstrike/console.log", Encoding.UTF8, TimeSpan.FromMilliseconds(20))
                               .ReadAsync(cancel.Token))
            {
                first.TrySetResult(chunk);
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(60, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_root, "cstrike"));
        await File.WriteAllTextAsync(Path.Combine(_root, "cstrike", "console.log"), "hello\n", TestContext.Current.CancellationToken);

        Assert.Equal("hello\n", await first.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await cancel.CancelAsync();
        await reading;
    }

    internal static async Task Eventually(Func<bool> condition, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what());
            await Task.Delay(20);
        }
    }
}

/// <summary>A Source game (logfile+rcon) under the supervisor: files prepared before the start, readiness and stop over RCON.</summary>
public sealed class LogfileRconSupervisorTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pulsar-rcon-supervisor", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeProcessManager _processes = new();
    private readonly RecordingEvents _events = new();
    private readonly FakeRcon _rcon = new();
    private ServerSupervisor? _supervisor;

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ConfigurationFilesAreWrittenAndTheOldLogRemovedBeforeTheStart()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cstrike", "cfg"));
        await File.WriteAllTextAsync(Path.Combine(_root, "cstrike", "cfg", "server.cfg"), "hostname \"old\"\nsv_cheats 0\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "cstrike", "console.log"), "previous run\n", TestContext.Current.CancellationToken);
        string? logAtStart = null;
        _processes.OnStarted = (_, _) => logAtStart = File.Exists(Path.Combine(_root, "cstrike", "console.log")) ? "present" : "gone";
        var supervisor = Supervisor(new RconReadiness(60));

        await supervisor.StartAsync();
        await _events.WaitForAsync(RuntimeState.Starting);
        await supervisor.FlushAsync();

        Assert.Equal("hostname \"Pulsar\"\nsv_cheats 0\n", await File.ReadAllTextAsync(Path.Combine(_root, "cstrike", "cfg", "server.cfg"),
            TestContext.Current.CancellationToken));
        Assert.Equal("gone", logAtStart);
        Assert.Equal(ConsoleMode.Pty, _processes.Specs[0].ConsoleMode); // srcds needs a console even though it writes nothing to it
    }

    [Fact]
    public async Task ItIsReadyOnceItAnswersOverRconAndStopsWithAnRconCommand()
    {
        var supervisor = Supervisor(new RconReadiness(60));
        await supervisor.StartAsync();
        await _events.WaitForAsync(RuntimeState.Starting);
        await supervisor.FlushAsync();
        Assert.Equal(RuntimeState.Starting, supervisor.Snapshot.State);

        _rcon.Listening = true;
        _clock.Advance(TimeSpan.FromSeconds(2));
        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);

        _rcon.OnCommand = command =>
        {
            if (command == "quit")
            {
                _processes.Last.Exit(0);
            }
        };
        await supervisor.StopAsync();
        await _events.WaitForAsync(RuntimeState.Offline, StateChangeReason.Requested);
        Assert.Equal(["quit"], _rcon.Commands);
        Assert.Equal(Rcon, _rcon.LastPassword);
    }

    [Fact]
    public async Task ALogPatternIsLookedForInTheLogFile()
    {
        var supervisor = Supervisor(new LogPatternReadiness("^Server is hibernating", 60));
        await supervisor.StartAsync();
        await _events.WaitForAsync(RuntimeState.Starting);
        await supervisor.FlushAsync();

        _processes.Last.Print("nothing on the console\r\n");
        Directory.CreateDirectory(Path.Combine(_root, "cstrike"));
        await File.WriteAllTextAsync(Path.Combine(_root, "cstrike", "console.log"), "Loading map\nServer is hibernating\n", TestContext.Current.CancellationToken);

        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
    }

    [Fact]
    public async Task AConfigurationFileThatCannotBeWrittenFailsTheStart()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cstrike", "cfg", "server.cfg")); // a directory where the file goes
        var supervisor = Supervisor(new RconReadiness(60));

        await supervisor.StartAsync();

        var failed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.StartFailed);
        Assert.StartsWith("config.", failed.Detail, StringComparison.Ordinal);
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task TheLogFileIsTheConsoleBeforeAndAfterTheServerIsReady()
    {
        var supervisor = await RunningAsync();
        await AppendLogAsync("Server is hibernating\n");

        await EventuallyAsync(supervisor, "Server is hibernating\n");
        await AppendLogAsync("Player connected\n");
        await EventuallyAsync(supervisor, "Server is hibernating\nPlayer connected\n");
        Assert.DoesNotContain("pseudo console", Text(supervisor), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RconCommandsAndTheirAnswersAreAddedForAGameThatDoesNotLogThem()
    {
        var supervisor = await RunningAsync();
        _rcon.Answer = command => command == "status" ? "hostname: Pulsar" : "";

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status"));
        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("say hi"));

        await EventuallyAsync(supervisor, "> status\nhostname: Pulsar\n> say hi\n");
        Assert.Equal(2, _rcon.Connections); // the readiness probe's, then one kept for both commands
        Assert.Equal(["status", "say hi"], _rcon.Commands);
    }

    [Fact]
    public async Task AGameThatLogsRconCommandsGetsNoSecondCopy()
    {
        var supervisor = await RunningAsync(logsCommands: true);
        _rcon.Answer = _ => "hostname: Pulsar";

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status"));

        await EventuallyAsync(() => _rcon.Commands.Contains("status"));
        await supervisor.FlushAsync();
        Assert.Equal("", Text(supervisor));
    }

    [Fact]
    public async Task AnRconFailureIsNotedInTheConsole()
    {
        var supervisor = await RunningAsync();
        _rcon.Listening = false;

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status"));

        await EventuallyAsync(() => Text(supervisor).Contains("[Pulsar] RCON: Not listening.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatTheGameLogsJustBeforeExitingIsKept()
    {
        var supervisor = await RunningAsync();
        _rcon.OnCommand = command =>
        {
            if (command == "quit")
            {
                // Written right before the exit, with no poll in between.
                File.AppendAllText(Path.Combine(_root, "cstrike", "console.log"), "Shutting down\n");
                _processes.Last.Exit(0);
            }
        };

        await supervisor.StopAsync();
        await _events.WaitForAsync(RuntimeState.Offline);

        await EventuallyAsync(supervisor, "Shutting down\n");
    }

    private const string Rcon = "Rc0nPassw0rd";

    private async Task<ServerSupervisor> RunningAsync(bool logsCommands = false)
    {
        var supervisor = Supervisor(new RconReadiness(60), logsCommands);
        _rcon.Listening = true;
        await supervisor.StartAsync();
        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
        Directory.CreateDirectory(Path.Combine(_root, "cstrike"));
        return supervisor;
    }

    private Task AppendLogAsync(string text) =>
        File.AppendAllTextAsync(Path.Combine(_root, "cstrike", "console.log"), text, TestContext.Current.CancellationToken);

    private static string Text(ServerSupervisor supervisor) => string.Concat(supervisor.Console.Buffer.Read(null, int.MaxValue).Select(e => e.Text));

    private static Task EventuallyAsync(ServerSupervisor supervisor, string text) =>
        LogTailTests.Eventually(() => Text(supervisor) == text, () => Text(supervisor));

    private static Task EventuallyAsync(Func<bool> condition) => LogTailTests.Eventually(condition, () => "condition not met");

    private ServerSupervisor Supervisor(ReadinessDefinition readiness, bool logsCommands = false)
    {
        var plan = new LaunchPlan("srcds.exe", "", ["-game", "cstrike"], new Dictionary<string, string>(),
            new ConsoleDefinition(ManifestConsoleMode.LogfileRcon, LogFile: "cstrike/console.log",
                Rcon: new RconDefinition(RconProtocol.Source, "game", logsCommands)),
            readiness, new StopDefinition([new RconCommandStop("quit")], 30))
        {
            ConfigFiles = [new RenderedConfigFile("cstrike/cfg/server.cfg", ConfigFileFormat.SourceCfg, ConfigFileMode.Patch,
                [new ConfigEntry("hostname", "Pulsar", Secret: false)])],
            Rcon = new RconEndpoint(RconProtocol.Source, "127.0.0.1", 27015, Rcon),
        };
        var definition = new ServerDefinition(Guid.NewGuid(), "abcd1234", _root, ProcessIdentity.CurrentUser, plan, AutoStart: false, RestartOnCrash: true);
        _supervisor = new ServerSupervisor(definition, _processes, _events, _clock,
            new SupervisorOptions { LogPollInterval = TimeSpan.FromMilliseconds(20) }, NullLogger.Instance, new DirectServerFiles(), _rcon);
        return _supervisor;
    }

    private sealed class FakeRcon : IRconConnector
    {
        public volatile bool Listening;

        public List<string> Commands { get; } = [];

        public Action<string>? OnCommand { get; set; }

        public string? LastPassword { get; private set; }

        public Func<string, string> Answer { get; set; } = _ => "";

        public int Connections { get; private set; }

        public Task<IRconSession> ConnectAsync(RconEndpoint endpoint, CancellationToken cancellationToken)
        {
            if (!Listening)
            {
                throw new RconException("Not listening.");
            }

            LastPassword = endpoint.Password;
            Connections++;
            return Task.FromResult<IRconSession>(new Session(this));
        }

        private sealed class Session(FakeRcon owner) : IRconSession
        {
            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            {
                lock (owner.Commands)
                {
                    owner.Commands.Add(command);
                }

                owner.OnCommand?.Invoke(command);
                return Task.FromResult(owner.Answer(command));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

public sealed class ServerStoreProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-store-protection", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RecordsAreStoredProtectedAndReadBack()
    {
        var store = new ServerStore(new AgentPaths(_root), new XorProtector());
        var spec = new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?> { ["gslt"] = "TOKEN-123" }, [], false, true);

        store.Save(new ServerRecord(Guid.NewGuid(), spec, true, false, "Rc0nPassw0rd"));

        var file = Directory.EnumerateFiles(Path.Combine(_root, "state", "servers")).Single();
        var raw = File.ReadAllText(file);
        Assert.DoesNotContain("TOKEN-123", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Rc0nPassw0rd", raw, StringComparison.Ordinal);
        Assert.Contains("\"protection\": \"xor\"", raw, StringComparison.Ordinal);
        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal("TOKEN-123", loaded.Spec.Variables["gslt"]);
        Assert.Equal("Rc0nPassw0rd", loaded.RconPassword);
        Assert.DoesNotContain("Rc0nPassw0rd", loaded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsWrittenBeforeEncryptionAreStillRead()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "state", "servers")).FullName;
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory, $"{id:N}.json"), $$"""
            { "serverId": "{{id}}", "spec": { "shortId": "abcd2345", "manifest": "{}", "variables": {}, "extraArguments": [], "autoStart": false,
              "restartOnCrash": true }, "desiredRunning": true, "resumeOnStart": false }
            """);

        var record = new ServerStore(new AgentPaths(_root), new XorProtector()).Load(id);

        Assert.True(record!.DesiredRunning);
    }

    private sealed class XorProtector : IStateProtector
    {
        public string Name => "xor";

        public byte[] Protect(byte[] data) => [.. data.Select(b => (byte)(b ^ 0x5A))];

        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
