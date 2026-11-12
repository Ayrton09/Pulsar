using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>GoldSrc's RCON over UDP against a fake hlds that answers as hlds 1.1.2.7 did (packets captured from it).</summary>
public sealed class GoldSrcRconTests
{
    private static readonly GoldSrcRconConnector s_connector = new() { AnswerIdle = TimeSpan.FromMilliseconds(150), AnswerTimeout = TimeSpan.FromSeconds(2) };

    [Fact]
    public async Task GetsAChallengeAndRunsCommands()
    {
        await using var game = new FakeHlds("secret");

        await using var session = await s_connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);
        var echo = await session.ExecuteAsync("echo hello", TestContext.Current.CancellationToken);

        Assert.Equal("hello \n", echo);
        Assert.Equal(["echo", "echo hello"], game.Commands); // the password is checked with a bare echo first
        Assert.All(game.Challenges, c => Assert.Equal(FakeHlds.Challenge, c));
    }

    [Fact]
    public async Task AnAnswerInSeveralPacketsAndSplitPartsIsReadWhole()
    {
        await using var game = new FakeHlds("secret");
        await using var session = await s_connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        var answer = await session.ExecuteAsync("cvarlist", TestContext.Current.CancellationToken);

        Assert.Equal("first packet\n" + "split one, " + "split two\n" + "last packet\n", answer);
    }

    [Fact]
    public async Task ACommandWithNoOutputAnswersEmpty()
    {
        await using var game = new FakeHlds("secret");
        await using var session = await s_connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        Assert.Equal("", await session.ExecuteAsync("nosuchcommand", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QuitAnswersNothingAndIsNoError()
    {
        // hlds closes without a word: the stop step must not report a game that did not answer.
        await using var game = new FakeHlds("secret");
        await using var session = await s_connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        Assert.Equal("", await session.ExecuteAsync("quit", TestContext.Current.CancellationToken));
        Assert.Contains("quit", game.Commands);
        await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("silent", TestContext.Current.CancellationToken)); // any other command
    }

    [Fact]
    public async Task AWrongPasswordIsReportedAsSuch()
    {
        await using var game = new FakeHlds("secret");

        var error = await Assert.ThrowsAsync<RconException>(() => s_connector.ConnectAsync(game.Endpoint("wrong"), ProcessIdentity.CurrentUser,
            TestContext.Current.CancellationToken));

        Assert.True(error.AuthenticationFailed);
    }

    [Fact]
    public async Task AChallengeTheGameNoLongerTakesEndsTheSessionWithoutRunningTheCommand()
    {
        await using var game = new FakeHlds("secret");
        await using var session = await s_connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);
        game.ForgetChallenges = true; // the game restarted

        var error = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("status", TestContext.Current.CancellationToken));

        Assert.True(error.ConnectionLost);
        Assert.True(error.NotSent);
        var again = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("status", TestContext.Current.CancellationToken));
        Assert.True(again.NotSent);
    }

    [Fact]
    public async Task NothingListeningIsAnRconError()
    {
        int port;
        using (var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)free.Client.LocalEndPoint!).Port;
        }

        var error = await Assert.ThrowsAsync<RconException>(() => s_connector.ConnectAsync(new RconEndpoint(RconProtocol.GoldSrc, "127.0.0.1", port, "x"),
            ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken));

        Assert.False(error.AuthenticationFailed);
        Assert.False(error.NotTheGame);
    }

    [Fact]
    public async Task AGameThatNeverStopsAnsweringIsCutAndItsSessionClosed()
    {
        using var pieces = new SemaphoreSlim(0);
        await using var game = new FakeHlds("secret") { NextTricklePacket = token => pieces.WaitAsync(token) };
        var clock = new RconTestClock();
        var idle = TimeSpan.FromSeconds(3);
        var connector = new GoldSrcRconConnector { AnswerIdle = idle, AnswerTimeout = TimeSpan.FromSeconds(5), Clock = clock };
        var connecting = connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync(idle);
        clock.Advance(idle); // The authentication echo ends by idle, before its overall deadline.
        await using var session = await connecting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var answering = session.ExecuteAsync("trickle", TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
        {
            pieces.Release();
            await clock.WaitForTimerAsync(idle); // The preceding packet was consumed and its idle wait is armed.
            clock.Advance(TimeSpan.FromSeconds(2));
            if (i < 4)
            {
                Assert.False(answering.IsCompleted); // No idle gap has expired; the answer must still be open.
            }
        }

        // Ten seconds total, while the latest idle deadline is still a second away: only the total limit can end it.
        var answer = await answering.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotEmpty(answer);
        var error = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("echo hello", TestContext.Current.CancellationToken));
        Assert.True(error.NotSent);
    }

    [Fact]
    public async Task ACallerCanCancelAnAnswerWithAControlledClock()
    {
        using var pieces = new SemaphoreSlim(0);
        await using var game = new FakeHlds("secret") { NextTricklePacket = token => pieces.WaitAsync(token) };
        var clock = new RconTestClock();
        var idle = TimeSpan.FromSeconds(3);
        var connector = new GoldSrcRconConnector { AnswerIdle = idle, Clock = clock };
        var connecting = connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync(idle);
        clock.Advance(idle);
        await using var session = await connecting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var answering = session.ExecuteAsync("trickle", cancelled.Token);
        pieces.Release();
        await clock.WaitForTimerAsync(idle);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answering.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    private sealed class RconTestClock : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

        public override long GetTimestamp() => _time.GetTimestamp();

        public override long TimestampFrequency => _time.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime);
            return timer;
        }

        public void Advance(TimeSpan time) => _time.Advance(time);

        public async Task WaitForTimerAsync(TimeSpan dueTime)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            waiting.CancelAfter(TimeSpan.FromSeconds(10));
            while (await _timers.Reader.ReadAsync(waiting.Token) != dueTime)
            {
                // Challenge, first-response and total-deadline timers precede the idle wait.
            }
        }
    }

    [Fact]
    public async Task ThePasswordGoesOnlyToAPortHeldByTheServersAccount()
    {
        // Another server's account bound to this server's port while it was stopped (docs/architecture/08, T18).
        await using var game = new FakeHlds("secret");
        var peers = new FakePeers { Owner = ProcessIdentity.Account("pulsar-other") };
        var connector = new GoldSrcRconConnector { Peers = peers };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.Account("pulsar-abcd2345"),
            TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
        Assert.Equal(game.Endpoint("secret").Port, peers.AskedUdp!.Port);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(game.Received); // not even the challenge
    }

    [Fact]
    public async Task APortTakenAfterTheSessionOpenedGetsNoMoreCommands()
    {
        // A process of another account binds the address the agent sends to while the game runs: each command checks again.
        await using var game = new FakeHlds("secret");
        var peers = new SwitchingPeers();
        var connector = new GoldSrcRconConnector { Peers = peers, AnswerIdle = TimeSpan.FromMilliseconds(150) };
        await using var session = await connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.Account("pulsar-abcd2345"),
            TestContext.Current.CancellationToken);
        Assert.Equal("hello \n", await session.ExecuteAsync("echo hello", TestContext.Current.CancellationToken));

        peers.Taken = true;
        var error = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("status", TestContext.Current.CancellationToken));
        peers.Taken = false;
        var closed = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("status", TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
        Assert.True(closed.NotSent); // the session is over: a new one checks again from the start
        Assert.DoesNotContain("status", game.Commands);
    }

    [Fact]
    public async Task AnotherProcessBoundToThePortBesideTheGameStopsThePasswordToo()
    {
        // A process bound to the address the agent sends to takes the datagrams before the game on every address does.
        await using var game = new FakeHlds("secret");
        var connector = new GoldSrcRconConnector
        {
            Peers = new FakePeers { Owner = ProcessIdentity.Account("pulsar-abcd2345"), OtherUdpOwner = ProcessIdentity.Account("pulsar-other") },
        };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.Account("pulsar-abcd2345"),
            TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
    }

    [Fact]
    public async Task APortNobodyHoldsYetIsAGameStillStartingAndGetsNothing()
    {
        // Not a warning: the game opens its port a moment after it starts. Nothing is sent meanwhile.
        await using var game = new FakeHlds("secret");
        var connector = new GoldSrcRconConnector { Peers = new FakePeers { Owner = null } };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser,
            TestContext.Current.CancellationToken));

        Assert.False(error.NotTheGame);
        Assert.False(error.AuthenticationFailed);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(game.Received);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APortCheckThatFailsIsNotTheGameEitherAndSendsNothing(bool failsFindingTheProcess)
    {
        await using var game = new FakeHlds("secret");
        var connector = new GoldSrcRconConnector { Peers = new ThrowingPeers(failsFindingTheProcess) };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser,
            TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(game.Received);
    }

    [Theory]
    [InlineData("quit\nsv_cheats 1")]
    [InlineData("")]
    public async Task CommandsAreSingleLines(string command)
    {
        await using var game = new FakeHlds("secret");
        await using var session = await s_connector.ConnectAsync(game.Endpoint("secret"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AGoldSrcGamesConsoleLogIsKeptFromTheFileManagerAndNoOtherGames()
    {
        Assert.Equal(["qconsole.log", "cstrike/logs"], ServerHost.PrivateFiles(Bundled("cs16")));
        Assert.Equal(["qconsole.log", "valve/logs"], ServerHost.PrivateFiles(Bundled("hldm")));
        Assert.Empty(ServerHost.PrivateFiles(Bundled("css"))); // Source RCON: the log holds no password
    }

    [Fact]
    public void ASplitPacketIsPutBackTogetherInAnyOrder()
    {
        var pending = new Dictionary<int, byte[]?[]>();
        byte[] whole = [0xFF, 0xFF, 0xFF, 0xFF, .. "lone two\n"u8, 0, 0];
        byte[] Part(int number, byte[] body) => [0xFE, 0xFF, 0xFF, 0xFF, 7, 0, 0, 0, (byte)((number << 4) | 2), .. body];

        Assert.Null(GoldSrcRconSession.Text(Part(1, whole[8..]), pending));
        Assert.Equal("one two\n", GoldSrcRconSession.Text(Part(0, whole[..8]), pending));
        Assert.Empty(pending);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF })] // too short
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0x30 })] // a query answer, not printed text
    [InlineData(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 1, 0, 0, 0, 0x22, 0x41 })] // part 2 of 2: no such part
    [InlineData(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 1, 0, 0, 0, 0x00, 0x41 })] // a split packet of no parts
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x6C, 0x41 })] // not GoldSrc
    public void PacketsThatAreNotAnAnswerAreSkipped(byte[] packet)
    {
        Assert.Null(GoldSrcRconSession.Text(packet, []));
    }

    private static GameManifest Bundled(string game)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        var result = ManifestParser.Parse(File.ReadAllBytes(Path.Combine(directory.FullName, "games", game, "manifest.json")));
        return result.Manifest ?? throw new InvalidOperationException(string.Join("; ", result.Errors));
    }
}

/// <summary>The game holds its port until <see cref="Taken"/>: then another account's process holds it beside the game.</summary>
internal sealed class SwitchingPeers : ILocalPeers
{
    public bool Taken { get; set; }

    public int? PeerProcess(IPEndPoint local, IPEndPoint remote) => null;

    public IReadOnlyList<int> UdpProcesses(IPAddress address, int port) => Taken ? [1, 2] : [1];

    public bool RunsAs(int processId, ProcessIdentity identity) => processId == 1 && identity == ProcessIdentity.Account("pulsar-abcd2345");
}

/// <summary>
/// hlds' RCON: <c>challenge rcon</c> answered with a number; <c>rcon &lt;n&gt; "&lt;password&gt;" &lt;command&gt;</c> answered with
/// printed packets, the way hlds 1.1.2.7 did (captured: <c>FFFFFFFF 6C &lt;text&gt; 00 00</c>; split parts <c>FEFFFFFF &lt;id&gt;
/// &lt;n&lt;&lt;4|total&gt;</c>).
/// </summary>
internal sealed class FakeHlds : IAsyncDisposable
{
    public const string Challenge = "1844759315";

    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly string _password;
    private readonly Task _loop;

    public FakeHlds(string password)
    {
        _password = password;
        _loop = ServeAsync();
    }

    public List<string> Commands { get; } = [];

    public List<string> Challenges { get; } = [];

    public List<string> Received { get; } = [];

    public bool ForgetChallenges { get; set; }

    public Func<CancellationToken, Task>? NextTricklePacket { get; init; }

    public RconEndpoint Endpoint(string password) =>
        new(RconProtocol.GoldSrc, "127.0.0.1", ((IPEndPoint)_udp.Client.LocalEndPoint!).Port, password);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _udp.Dispose();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var datagram = await _udp.ReceiveAsync(_stop.Token);
            var text = Encoding.UTF8.GetString(datagram.Buffer.AsSpan(4)).TrimEnd('\n');
            lock (Received)
            {
                Received.Add(text);
            }

            var to = datagram.RemoteEndPoint;
            if (text == "challenge rcon")
            {
                await SendAsync(Single($"challenge rcon {Challenge}\n", print: false), to);
                continue;
            }

            var parts = text.Split(' ', 4);
            if (parts is not ["rcon", var challenge, var quoted, var command])
            {
                continue;
            }

            Challenges.Add(challenge);
            if (ForgetChallenges)
            {
                await SendAsync(Single("Bad challenge.\n"), to);
                continue;
            }

            if (quoted != $"\"{_password}\"")
            {
                await SendAsync(Single("Bad rcon_password.\n"), to);
                continue;
            }

            Commands.Add(command);
            switch (command)
            {
                case "echo":
                    await SendAsync(Single("\n"), to);
                    break;
                case "nosuchcommand":
                    await SendAsync(Single(""), to);
                    break;
                case "cvarlist":
                    await SendAsync(Single("first packet\n"), to);
                    var split = Single("split one, split two\n");
                    await SendAsync([0xFE, 0xFF, 0xFF, 0xFF, 2, 0, 0, 0, 0x02, .. split[..16]], to);
                    await SendAsync([0xFE, 0xFF, 0xFF, 0xFF, 2, 0, 0, 0, 0x12, .. split[16..]], to);
                    await SendAsync(Single("last packet\n"), to);
                    break;
                case "trickle":
                    for (var i = 0; i < 200 && !_stop.IsCancellationRequested; i++)
                    {
                        if (NextTricklePacket is { } next)
                        {
                            await next(_stop.Token);
                        }

                        await SendAsync(Single("more\n"), to);
                        if (NextTricklePacket is null)
                        {
                            await Task.Delay(20, _stop.Token);
                        }
                    }

                    break;
                default:
                    if (command.StartsWith("echo ", StringComparison.Ordinal))
                    {
                        await SendAsync(Single(command[5..] + " \n"), to);
                    }

                    break;
            }
        }
    }

    // hlds' printed packet: header, 'l', the text and two NULs; the challenge answer has no 'l'.
    private static byte[] Single(string text, bool print = true) =>
        [0xFF, 0xFF, 0xFF, 0xFF, .. print ? "l"u8 : [], .. Encoding.UTF8.GetBytes(text), 0, 0];

    private async Task SendAsync(byte[] packet, IPEndPoint to)
    {
        try
        {
            await _udp.SendAsync(packet, to, _stop.Token);
        }
        catch (SocketException)
        {
            // The client went away.
        }
    }
}
