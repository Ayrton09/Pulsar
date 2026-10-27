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

        await using var session = await connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);
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

        var error = await Assert.ThrowsAsync<RconException>(() => new SourceRconConnector().ConnectAsync(game.Endpoint("wrong"), ProcessIdentity.CurrentUser,
            TestContext.Current.CancellationToken));

        Assert.True(error.AuthenticationFailed);
    }

    [Fact]
    public async Task NothingListeningIsAnRconError()
    {
        var port = FreePort();

        var error = await Assert.ThrowsAsync<RconException>(() => new SourceRconConnector().ConnectAsync(
            new RconEndpoint(RconProtocol.Source, "127.0.0.1", port, "x"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken));

        Assert.False(error.AuthenticationFailed);
    }

    [Theory]
    [InlineData("quit\nsv_cheats 1")]
    [InlineData("")]
    public async Task CommandsAreSingleLines(string command)
    {
        await using var game = new FakeRconServer("secret", srcds: false);
        await using var session = await new SourceRconConnector().ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APacketThatArrivesInPartsIsReadWhole()
    {
        // The idle wait that ends an answer must not cut a packet between its header and its body.
        await using var game = new FakeRconServer("secret", srcds: false);
        var connector = new SourceRconConnector { AnswerIdle = TimeSpan.FromMilliseconds(100) };
        await using var session = await connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        Assert.Equal("part one, part two", await session.ExecuteAsync("split", TestContext.Current.CancellationToken));
        Assert.Equal("hello\n", await session.ExecuteAsync("echo hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public Task AnAnswerThatNeverPausesIsCutAfterItsTime() => AnAnswerThatNeverEndsIsCutAndItsSessionClosedAsync("trickle");

    [Fact]
    public Task AnAnswerThatNeverEndsIsCutAtItsSize() => AnAnswerThatNeverEndsIsCutAndItsSessionClosedAsync("flood");

    private static async Task AnAnswerThatNeverEndsIsCutAndItsSessionClosedAsync(string command)
    {
        await using var game = new FakeRconServer("secret", srcds: false);
        // The pause that ends an answer is far longer than the game's 20 ms between pieces, even on a loaded machine: the
        // answer must be cut by its time or size, never end by itself.
        var connector = new SourceRconConnector { AnswerIdle = TimeSpan.FromSeconds(3), AnswerTimeout = TimeSpan.FromSeconds(1) };
        await using var session = await connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        var answer = await session.ExecuteAsync(command, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(answer);
        Assert.True(answer.Length <= 1024 * 1024, $"{answer.Length} characters kept");
        var error = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("echo hello", TestContext.Current.CancellationToken));
        Assert.True(error.ConnectionLost);
    }

    [Fact]
    public async Task ASessionTheGameClosedSaysTheCommandWasNotSent()
    {
        await using var game = new FakeRconServer("secret", srcds: false);
        var connector = new SourceRconConnector { AnswerIdle = TimeSpan.FromMilliseconds(100) };
        await using var session = await connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);
        Assert.Equal("bye\n", await session.ExecuteAsync("bye", TestContext.Current.CancellationToken));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("echo hello", TestContext.Current.CancellationToken));

        Assert.True(error.ConnectionLost);
        Assert.True(error.NotSent);
        Assert.Equal(["bye"], game.Commands);
    }

    [Fact]
    public async Task AConnectionLostAfterTheCommandWasSentSaysSo()
    {
        await using var game = new FakeRconServer("secret", srcds: false);
        await using var session = await new SourceRconConnector().ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("crash", TestContext.Current.CancellationToken));

        Assert.True(error.ConnectionLost);
        Assert.False(error.NotSent); // the game got it: it may have run
    }

    [Fact]
    public async Task ThePasswordGoesOnlyToAProcessRunningAsTheServersAccount()
    {
        // Another server's account listening on this server's port while it was stopped (docs/architecture/08, T18).
        await using var game = new FakeRconServer("secret", srcds: true);
        var peers = new FakePeers { Owner = ProcessIdentity.Account("pulsar-other") };
        var connector = new SourceRconConnector { Peers = peers };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.Account("pulsar-abcd2345"),
            TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
        Assert.False(error.AuthenticationFailed);
        Assert.Equal(game.Endpoint("secret").Port, peers.Asked!.Value.Remote.Port); // the game's end of this very connection
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(game.Logons);
    }

    [Fact]
    public async Task TheServersOwnGameGetsThePassword()
    {
        await using var game = new FakeRconServer("secret", srcds: false);
        var connector = new SourceRconConnector { Peers = new FakePeers { Owner = ProcessIdentity.Account("pulsar-abcd2345") } };

        await using var session = await connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.Account("pulsar-abcd2345"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["secret"], game.Logons);
    }

    [Fact]
    public async Task AConnectionNoLocalProcessHoldsIsNotTheGame()
    {
        await using var game = new FakeRconServer("secret", srcds: false);
        var connector = new SourceRconConnector { Peers = new FakePeers { Owner = null } };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser,
            TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APeerCheckThatFailsIsNotTheGameEitherAndSendsNothing(bool failsFindingTheProcess)
    {
        // The operating system could not say who holds the connection (a table it would not return, an account it cannot look up).
        await using var game = new FakeRconServer("secret", srcds: false);
        var connector = new SourceRconConnector { Peers = new ThrowingPeers(failsFindingTheProcess) };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser,
            TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
        Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(game.Logons);
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Who listens: a process running as <see cref="Owner"/>, or none (null).</summary>
internal sealed class FakePeers : ILocalPeers
{
    private const int Process = 4242;

    public ProcessIdentity? Owner { get; init; }

    public (IPEndPoint Local, IPEndPoint Remote)? Asked { get; private set; }

    public int? PeerProcess(IPEndPoint local, IPEndPoint remote)
    {
        Asked = (local, remote);
        return Owner is null ? null : Process;
    }

    public bool RunsAs(int processId, ProcessIdentity identity) => processId == Process && identity == Owner;
}

/// <summary>A machine that cannot tell who holds a connection.</summary>
internal sealed class ThrowingPeers(bool failsFindingTheProcess) : ILocalPeers
{
    public int? PeerProcess(IPEndPoint local, IPEndPoint remote) =>
        failsFindingTheProcess ? throw new System.ComponentModel.Win32Exception(5) : 4242;

    public bool RunsAs(int processId, ProcessIdentity identity) => throw new System.ComponentModel.Win32Exception(1332);
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

    /// <summary>The passwords it was sent.</summary>
    public List<string> Logons { get; } = [];

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
                Logons.Add(body);
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
                case "split":
                    // The second packet's header, then a pause longer than the client's idle wait, then its body.
                    await SendAsync(stream, id, 0, "part one, ");
                    var packet = Packet(id, 0, "part two");
                    await stream.WriteAsync(packet.AsMemory(0, 4), _stop.Token);
                    await Task.Delay(300, _stop.Token);
                    await stream.WriteAsync(packet.AsMemory(4), _stop.Token);
                    break;
                case "trickle":
                    // A plugin that keeps answering, a little at a time and never pausing long enough to end the answer.
                    while (!_stop.IsCancellationRequested)
                    {
                        await SendAsync(stream, id, 0, "z");
                        await Task.Delay(20, _stop.Token);
                    }

                    break;
                case "flood":
                    while (!_stop.IsCancellationRequested)
                    {
                        await SendAsync(stream, id, 0, new string('f', 60_000));
                    }

                    break;
                case "bye":
                    // Answered, then the session is closed once idle (a game that drops idle RCON clients).
                    await SendAsync(stream, id, 0, "bye\n");
                    await Task.Delay(300, _stop.Token);
                    return;
                case "crash":
                    return; // closed before answering

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

    private async Task SendAsync(NetworkStream stream, int id, int type, string body) => await stream.WriteAsync(Packet(id, type, body), _stop.Token);

    private static byte[] Packet(int id, int type, string body)
    {
        var text = Encoding.UTF8.GetBytes(body);
        var packet = new byte[14 + text.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet.AsSpan(12));
        return packet;
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
    public async Task ALogTheGameRotatesIsFollowedIntoItsNewFile()
    {
        // The game renames its log and starts a new one under the same name: the console goes on with the new one.
        var path = Path.Combine(_root, "console.log");
        await File.WriteAllTextAsync(path, "first file\n", TestContext.Current.CancellationToken);
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
        await Eventually(() => Read(text) == "first file\n", () => Read(text));

        File.Move(path, path + ".1");
        await File.WriteAllTextAsync(path, "second file, longer than the first\n", TestContext.Current.CancellationToken);

        await Eventually(() => Read(text) == "first file\nsecond file, longer than the first\n", () => Read(text));
        await cancel.CancelAsync();
        await reading;
    }

    private static string Read(StringBuilder text)
    {
        lock (text)
        {
            return text.ToString();
        }
    }

    [Fact]
    public async Task StartsWhereAnEarlierReaderStoppedAndSaysHowFarItRead()
    {
        var path = Path.Combine(_root, "console.log");
        await File.WriteAllTextAsync(path, "old line\nnew line\n", TestContext.Current.CancellationToken);
        var tail = new LogTail(new DirectServerFiles(), _root, "console.log", Encoding.UTF8, TimeSpan.FromMilliseconds(20), start: 9);

        Assert.Equal("new line\n", await FirstAsync(tail));
        Assert.Equal(18, tail.Position);
    }

    [Fact]
    public async Task APositionNeverFallsInsideACharacter()
    {
        // The game wrote the first byte of "€" (three in UTF-8) when the agent read: the next reader starts before it.
        var path = Path.Combine(_root, "console.log");
        var euro = Encoding.UTF8.GetBytes("€");
        await File.WriteAllBytesAsync(path, [.. "price "u8.ToArray(), euro[0]], TestContext.Current.CancellationToken);
        var first = new LogTail(new DirectServerFiles(), _root, "console.log", Encoding.UTF8, TimeSpan.FromMilliseconds(20));
        Assert.Equal("price ", await FirstAsync(first));
        Assert.Equal(6, first.Position);

        await using (var game = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            await game.WriteAsync(euro.AsMemory(1), TestContext.Current.CancellationToken);
            await game.WriteAsync("\n"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        var next = new LogTail(new DirectServerFiles(), _root, "console.log", Encoding.UTF8, TimeSpan.FromMilliseconds(20), first.Position);
        Assert.Equal("€\n", await FirstAsync(next));
        Assert.Equal(10, next.Position);
    }

    private static async Task<string> FirstAsync(LogTail tail)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(5));
        await foreach (var chunk in tail.ReadAsync(cancel.Token))
        {
            return chunk;
        }

        throw new TimeoutException("Nothing was read.");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileThatCannotBeOpenedYetIsTriedAgainAtTheNextPoll(bool denied)
    {
        // Held by the game without sharing for a moment (or its rules not set yet): the log is still followed.
        await File.WriteAllTextAsync(Path.Combine(_root, "console.log"), "hello\n", TestContext.Current.CancellationToken);
        var files = new FlakyServerFiles(failures: 3, denied);

        Assert.Equal("hello\n", await FirstAsync(new LogTail(files, _root, "console.log", Encoding.UTF8, TimeSpan.FromMilliseconds(20))));
        Assert.Equal(4, files.Opened);
    }

    [Fact]
    public async Task AFileThatKeepsRefusingToOpenIsReportedOnceAndStillFollowedWhenItOpens()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "console.log"), "hello\n", TestContext.Current.CancellationToken);
        var files = new FlakyServerFiles(failures: 30, denied: true);
        List<Exception> reported = [];
        var tail = new LogTail(files, _root, "console.log", Encoding.UTF8, TimeSpan.FromMilliseconds(5), onUnreadable: reported.Add);

        Assert.Equal("hello\n", await FirstAsync(tail));

        var reason = Assert.Single(reported); // not on the first failures (the game may be creating it), and once only
        Assert.IsType<UnauthorizedAccessException>(reason);
    }

    [Fact]
    public async Task AFileThatOpensAfterAFewFailuresIsNotReported()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "console.log"), "hello\n", TestContext.Current.CancellationToken);
        List<Exception> reported = [];

        Assert.Equal("hello\n", await FirstAsync(new LogTail(new FlakyServerFiles(failures: 3, denied: false), _root, "console.log", Encoding.UTF8,
            TimeSpan.FromMilliseconds(5), onUnreadable: reported.Add)));

        Assert.Empty(reported);
    }

    private sealed class FlakyServerFiles(int failures, bool denied) : IServerFiles
    {
        private readonly DirectServerFiles _inner = new();

        public int Opened { get; private set; }

        public Stream? OpenRead(string serverRoot, string relativePath)
        {
            if (++Opened <= failures)
            {
                throw denied ? new UnauthorizedAccessException("denied") : new IOException("in use");
            }

            return _inner.OpenRead(serverRoot, relativePath);
        }

        public void Write(string serverRoot, string relativePath, Stream content) => _inner.Write(serverRoot, relativePath, content);

        public void Delete(string serverRoot, string relativePath) => _inner.Delete(serverRoot, relativePath);

        public void CreateDirectory(string serverRoot, string relativePath) => _inner.CreateDirectory(serverRoot, relativePath);

        public bool DeleteEmptyDirectory(string serverRoot, string relativePath) => _inner.DeleteEmptyDirectory(serverRoot, relativePath);

        public IReadOnlyList<(string Name, DateTimeOffset LastWrite)> ListFiles(string serverRoot, string relativeDirectory, int limit) =>
            _inner.ListFiles(serverRoot, relativeDirectory, limit);
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
    public async Task SomethingElseOnItsRconPortNeverMakesItReady()
    {
        // Another server's account listens there: no password goes to it, and the console says why it waits.
        var supervisor = Supervisor(new RconReadiness(60));
        _rcon.Listening = true;
        _rcon.NotTheGame = true;
        await supervisor.StartAsync();
        await _events.WaitForAsync(RuntimeState.Starting);
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await supervisor.FlushAsync();
        Assert.Equal(RuntimeState.Starting, supervisor.Snapshot.State);
        Assert.Null(_rcon.LastPassword);
        await EventuallyAsync(() => Text(supervisor).Contains("is not the server's game", StringComparison.Ordinal));
        Assert.Single(Text(supervisor).Split('\n'), line => line.Contains("is not the server's game", StringComparison.Ordinal));
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
    public async Task ALogNamedByAPatternIsTheFileThatAppearedWithTheStart()
    {
        // Space Engineers names its log by the time it started: an earlier start's log stays, and is not this start's console.
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_root, "cstrike"));
        await File.WriteAllTextAsync(Path.Combine(_root, "cstrike", "console_1.log"), "Server is hibernating\n", ct);
        var supervisor = Supervisor(new LogPatternReadiness("^Server is hibernating", 60),
            change: plan => plan with { Console = plan.Console with { LogFile = "cstrike/console_*.log" } });
        await supervisor.StartAsync();
        await _events.WaitForAsync(RuntimeState.Starting);

        await Task.Delay(200, ct); // polls enough to have taken the earlier log, were it taken
        await supervisor.FlushAsync();
        Assert.DoesNotContain("hibernating", Text(supervisor), StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(_root, "cstrike", "console_2.log"), "Loading map\nServer is hibernating\n", ct);
        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
        await EventuallyAsync(() => Text(supervisor).EndsWith("Loading map\nServer is hibernating\n", StringComparison.Ordinal));
        Assert.Single(Text(supervisor).Split("hibernating")[1..]);
        Assert.True(File.Exists(Path.Combine(_root, "cstrike", "console_1.log")));
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
    public async Task AConfigurationFileThatIsNotUtf8IsLeftAsItIsAndFailsTheStart()
    {
        // A server.cfg saved in Windows-1252: rewriting it as UTF-8 would turn the user's "é" into U+FFFD.
        var path = Path.Combine(_root, "cstrike", "cfg", "server.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var latin = Encoding.Latin1.GetBytes("// mi café\nhostname \"old\"\n");
        await File.WriteAllBytesAsync(path, latin, TestContext.Current.CancellationToken);
        var supervisor = Supervisor(new RconReadiness(60));

        await supervisor.StartAsync();

        var failed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.StartFailed);
        Assert.Equal("config.invalid", failed.Detail);
        Assert.Equal(latin, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(_processes.Started);
        Assert.Contains("cstrike/cfg/server.cfg is not UTF-8 text, so Pulsar does not change it: save it as UTF-8.", Text(supervisor), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public async Task AConfigurationFileInAnotherUnicodeEncodingIsLeftAsItIsAndFailsTheStart(string encoding)
    {
        // Its byte order mark says what it is, but it is not UTF-8: rewriting it as UTF-8 would change its encoding.
        var path = Path.Combine(_root, "cstrike", "cfg", "server.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var unicode = Encoding.GetEncoding(encoding);
        byte[] content = [.. unicode.GetPreamble(), .. unicode.GetBytes("hostname \"old\"\n")];
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        var supervisor = Supervisor(new RconReadiness(60));

        await supervisor.StartAsync();

        var failed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.StartFailed);
        Assert.Equal("config.invalid", failed.Detail);
        Assert.Equal(content, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public async Task AFileAnAddonEditsThatIsNotUtf8IsLeftAsItIsAndFailsTheStart()
    {
        // A gameinfo.gi saved in Windows-1252: Metamod's line must not cost the user's other characters.
        var path = Path.Combine(_root, "game", "csgo", "gameinfo.gi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var latin = Encoding.Latin1.GetBytes("// mi café\nGame_LowViolence csgo_lv\n");
        await File.WriteAllBytesAsync(path, latin, TestContext.Current.CancellationToken);
        var supervisor = Supervisor(new RconReadiness(60), change: plan => plan with
        {
            LineEdits = [new LineEdit("game/csgo/gameinfo.gi", "Game_LowViolence csgo_lv", "Game\tcsgo/addons/metamod")],
        });

        await supervisor.StartAsync();

        var failed = await _events.WaitForAsync(RuntimeState.Crashed, StateChangeReason.StartFailed);
        Assert.Equal("config.invalid", failed.Detail);
        Assert.Equal(latin, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(_processes.Started);
        Assert.Contains("game/csgo/gameinfo.gi is not UTF-8 text, so Pulsar does not change it: save it as UTF-8.", Text(supervisor), StringComparison.Ordinal);
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

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status", TestContext.Current.CancellationToken));
        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("say hi", TestContext.Current.CancellationToken));

        await EventuallyAsync(supervisor, "> status\nhostname: Pulsar\n> say hi\n");
        Assert.Equal(2, _rcon.Connections); // the readiness probe's, then one kept for both commands
        Assert.Equal(["status", "say hi"], _rcon.Commands);
    }

    [Fact]
    public async Task AGameThatLogsRconCommandsGetsNoSecondCopy()
    {
        // The log is written before the answer comes back, but the agent sees it at its next poll of the file: how long it
        // waits for that is generous here (a busy machine); it stops waiting as soon as the line is seen.
        var supervisor = await RunningAsync(logsCommands: true, loggedAnswerWait: TimeSpan.FromSeconds(10));
        _rcon.Answer = _ => "hostname: Pulsar\nplayers : 0 humans";
        _rcon.OnCommand = command => File.AppendAllText(Path.Combine(_root, "cstrike", "console.log"),
            $"rcon from \"127.0.0.1:50000\": command \"{command}\"\nhostname: Pulsar\nplayers : 0 humans\n");

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status", TestContext.Current.CancellationToken));

        const string Logged = "rcon from \"127.0.0.1:50000\": command \"status\"\nhostname: Pulsar\nplayers : 0 humans\n";
        await EventuallyAsync(supervisor, Logged);
        await Task.Delay(s_loggedAnswerWait * 3, TestContext.Current.CancellationToken);
        await supervisor.FlushAsync();
        Assert.Equal(Logged, Text(supervisor));
    }

    [Fact]
    public async Task AnAnswerAGameThatLogsCommandsLeavesOutIsShown()
    {
        // CS2 logs "status" and its answer, but not the answer to a cvar query.
        var supervisor = await RunningAsync(logsCommands: true);
        _rcon.Answer = command => command == "sv_tags" ? "  sv_tags = pulsar,casual" : "";
        _rcon.OnCommand = command => File.AppendAllText(Path.Combine(_root, "cstrike", "console.log"),
            $"rcon from \"127.0.0.1:50000\": command \"{command}\"\n");

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("sv_tags", TestContext.Current.CancellationToken));

        await EventuallyAsync(supervisor, "rcon from \"127.0.0.1:50000\": command \"sv_tags\"\n  sv_tags = pulsar,casual\n");
    }

    [Fact]
    public async Task AnRconFailureIsNotedInTheConsole()
    {
        var supervisor = await RunningAsync();
        _rcon.Listening = false;

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status", TestContext.Current.CancellationToken));

        await EventuallyAsync(() => Text(supervisor).Contains("[Pulsar] RCON: Not listening.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACommandOnAKeptSessionTheGameHadClosedIsSentOnceMoreOnAFreshOne()
    {
        var supervisor = await RunningAsync();
        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status", TestContext.Current.CancellationToken));
        await EventuallyAsync(() => _rcon.Commands.Count == 1);
        var refusals = 1; // the kept session: the game closed it meanwhile
        _rcon.Refuse = _ => refusals-- > 0 ? new RconException("The RCON connection was closed.") { ConnectionLost = true, NotSent = true } : null;

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("say hi", TestContext.Current.CancellationToken));

        await EventuallyAsync(() => _rcon.Commands.Count == 2);
        Assert.Equal(["status", "say hi"], _rcon.Commands);
        Assert.Equal(3, _rcon.Connections); // the probe's, the kept one, the fresh one
    }

    [Fact]
    public async Task ACommandWhoseAnswerWasCutOffIsNotSentAgain()
    {
        // The game took the command and the connection broke before its answer: it may have run, so it never runs twice.
        var supervisor = await RunningAsync();
        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status", TestContext.Current.CancellationToken));
        await EventuallyAsync(() => _rcon.Commands.Count == 1);
        _rcon.OnCommand = command =>
        {
            if (command == "say hi")
            {
                throw new RconException("The RCON connection was closed.") { ConnectionLost = true };
            }
        };

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("say hi", TestContext.Current.CancellationToken));

        await EventuallyAsync(() => Text(supervisor).Contains("[Pulsar] RCON: The RCON connection was closed.", StringComparison.Ordinal));
        Assert.Equal(["status", "say hi"], _rcon.Commands);
        Assert.Equal(2, _rcon.Connections);
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

    [Fact]
    public async Task AGameTakenOverFromItsHostContinuesItsLogWhereThePreviousAgentStopped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cstrike"));
        await AppendLogAsync("Server is hibernating\nPlayer connected\n");
        var read = "Server is hibernating\n".Length; // what the previous run of the agent had shown
        var supervisor = Supervisor(new RconReadiness(60));
        var game = new FakeGameProcess(5000);
        var host = new GameHostReference("pulsar-host-test", 4000, DateTimeOffset.UtcNow);

        Assert.True(await supervisor.AttachAsync(new FakeHostedProcess(game, host), new GameHostState(host, 0, read, Ready: true), desiredRunning: true));

        await EventuallyAsync(supervisor, "Player connected\n");
        await AppendLogAsync("Player disconnected\n");
        await EventuallyAsync(supervisor, "Player connected\nPlayer disconnected\n");
        var length = new FileInfo(Path.Combine(_root, "cstrike", "console.log")).Length;
        await EventuallyAsync(() => supervisor.HostState?.LogPosition == length); // where the next agent goes on
        Assert.Equal(RuntimeState.Running, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task AProcessConsoleCanTakeItsCommandsThroughRcon()
    {
        // Palworld: its output on its own console, no input there; commands go through RCON.
        var supervisor = await RunningAsync(mode: ManifestConsoleMode.Pty);
        _rcon.Answer = command => command == "ShowPlayers" ? "name,playeruid,steamid" : "";

        _processes.Last.Print("Running Palworld dedicated server on :8211\r\n");
        // The game's output is read on its own: once it is in, the command's lines cannot land in the middle of it.
        await LogTailTests.Eventually(() => Text(supervisor).Contains("Running Palworld dedicated server on :8211", StringComparison.Ordinal),
            () => Text(supervisor));
        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("ShowPlayers", TestContext.Current.CancellationToken));

        await LogTailTests.Eventually(() => Text(supervisor).Contains("> ShowPlayers\nname,playeruid,steamid\n", StringComparison.Ordinal),
            () => Text(supervisor));
        Assert.Equal(["ShowPlayers"], _rcon.Commands);
        Assert.Empty(_processes.Last.Input); // nothing typed into the game's console
    }

    [Fact]
    public async Task AGameThatPrintsItsRconCommandsToItsOutputGetsNoSecondCopy()
    {
        // Soulmask, run with pipes, prints each RCON command and its answer itself.
        var ct = TestContext.Current.CancellationToken;
        var supervisor = await RunningAsync(logsCommands: true, mode: ManifestConsoleMode.Pipes, loggedAnswerWait: TimeSpan.FromSeconds(10));
        _rcon.Answer = _ => "Average FPS: 30";
        _rcon.OnCommand = command => _processes.Last.Print($"TRY RUN ADMIN COMMAND: {command}\r\nRESPONSE: Average FPS: 30\r\n");

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("fps", ct));

        const string Printed = "TRY RUN ADMIN COMMAND: fps\r\nRESPONSE: Average FPS: 30\r\n";
        await EventuallyAsync(supervisor, Printed);
        await Task.Delay(s_loggedAnswerWait * 3, ct);
        await supervisor.FlushAsync();
        Assert.Equal(Printed, Text(supervisor));
    }

    private const string Rcon = "Rc0nPassw0rd";

    private static readonly TimeSpan s_loggedAnswerWait = TimeSpan.FromMilliseconds(300);

    private async Task<ServerSupervisor> RunningAsync(bool logsCommands = false, ManifestConsoleMode mode = ManifestConsoleMode.LogfileRcon,
        TimeSpan? loggedAnswerWait = null)
    {
        var supervisor = Supervisor(new RconReadiness(60), logsCommands, mode, loggedAnswerWait);
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

    private ServerSupervisor Supervisor(ReadinessDefinition readiness, bool logsCommands = false, ManifestConsoleMode mode = ManifestConsoleMode.LogfileRcon,
        TimeSpan? loggedAnswerWait = null, Func<LaunchPlan, LaunchPlan>? change = null)
    {
        var plan = new LaunchPlan("srcds.exe", "", ["-game", "cstrike"], new Dictionary<string, string>(),
            new ConsoleDefinition(mode, LogFile: mode == ManifestConsoleMode.LogfileRcon ? "cstrike/console.log" : null,
                Rcon: new RconDefinition(RconProtocol.Source, "game", logsCommands)),
            readiness, new StopDefinition([new RconCommandStop("quit")], 30))
        {
            ConfigFiles = [new RenderedConfigFile("cstrike/cfg/server.cfg", ConfigFileFormat.SourceCfg, ConfigFileMode.Patch,
                [new ConfigEntry("hostname", "Pulsar", Secret: false)])],
            Rcon = new RconEndpoint(RconProtocol.Source, "127.0.0.1", 27015, Rcon),
        };
        var definition = new ServerDefinition(Guid.NewGuid(), "abcd1234", _root, ProcessIdentity.CurrentUser, change?.Invoke(plan) ?? plan, AutoStart: false,
            RestartOnCrash: true);
        _supervisor = new ServerSupervisor(definition, _processes, _events, _clock,
            new SupervisorOptions { LogPollInterval = TimeSpan.FromMilliseconds(20), LoggedAnswerWait = loggedAnswerWait ?? s_loggedAnswerWait }, NullLogger.Instance, new DirectServerFiles(), _rcon);
        return _supervisor;
    }

    private sealed class FakeRcon : IRconConnector
    {
        public volatile bool Listening;

        /// <summary>Something else than the game listens (another server's account).</summary>
        public volatile bool NotTheGame;

        public List<string> Commands { get; } = [];

        public Action<string>? OnCommand { get; set; }

        /// <summary>A failure before the game got the command (a session it had closed).</summary>
        public Func<string, RconException?>? Refuse { get; set; }

        public string? LastPassword { get; private set; }

        public Func<string, string> Answer { get; set; } = _ => "";

        public int Connections { get; private set; }

        public Task<IRconSession> ConnectAsync(RconEndpoint endpoint, ProcessIdentity owner, CancellationToken cancellationToken)
        {
            if (!Listening)
            {
                throw new RconException("Not listening.");
            }

            if (NotTheGame)
            {
                throw new RconException("What listens at 127.0.0.1:27015 is not the server's game.") { NotTheGame = true };
            }

            LastPassword = endpoint.Password;
            Connections++;
            return Task.FromResult<IRconSession>(new Session(this));
        }

        private sealed class Session(FakeRcon owner) : IRconSession
        {
            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            {
                if (owner.Refuse?.Invoke(command) is { } refused)
                {
                    throw refused;
                }

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
