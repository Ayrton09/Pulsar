using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

public sealed class ConsoleBufferTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SequencesStartAtTheCreationTimeSoALaterAgentContinuesAboveTheEarlierOne()
    {
        var clock = new FakeTimeProvider(s_start);
        var earlier = new ConsoleBuffer(clock);
        for (var i = 0; i < 100; i++)
        {
            earlier.Append($"line {i}\n", s_start.AddSeconds(i)); // a second apart: one entry each
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        var later = new ConsoleBuffer(clock);
        later.Append("first line of the new run\n", clock.GetUtcNow());

        Assert.Equal(s_start.ToUnixTimeMilliseconds() * 1000, earlier.Read(null, int.MaxValue)[0].Sequence);
        Assert.True(later.Read(null, int.MaxValue)[0].Sequence > earlier.LastSequence);
    }

    [Fact]
    public void PiecesCloseInTimeJoinOneEntryUntilItIsRead()
    {
        var buffer = new ConsoleBuffer(new FakeTimeProvider(s_start));
        buffer.Append("Load", s_start);
        buffer.Append("ing map\n", s_start.AddMilliseconds(30));

        var first = Assert.Single(buffer.Read(null, int.MaxValue));
        Assert.Equal("Loading map\n", first.Text);

        // Read entries never change: what follows is a new one, even this close in time.
        buffer.Append("done\n", s_start.AddMilliseconds(40));
        var all = buffer.Read(null, int.MaxValue);
        Assert.Equal(["Loading map\n", "done\n"], all.Select(e => e.Text));
        Assert.Equal(first.Sequence + 1, all[1].Sequence);
        Assert.Equal(all[1], Assert.Single(buffer.Read(first.Sequence, int.MaxValue)));
        Assert.Empty(buffer.Read(all[1].Sequence, int.MaxValue));
    }

    [Fact]
    public void PiecesFurtherApartThanTheMergeWindowAreSeparateEntries()
    {
        var buffer = new ConsoleBuffer(new FakeTimeProvider(s_start));
        buffer.Append("a", s_start);
        buffer.Append("b", s_start + ConsoleBuffer.MergeWindow + TimeSpan.FromMilliseconds(1));

        Assert.Equal(["a", "b"], buffer.Read(null, int.MaxValue).Select(e => e.Text));
    }

    [Fact]
    public void LongOutputIsSplitWithoutBreakingACharacter()
    {
        var buffer = new ConsoleBuffer(new FakeTimeProvider(s_start));
        // A surrogate pair right on the entry boundary.
        var text = new string('x', ConsoleBuffer.MaxEntryLength - 1) + "😀" + "tail";

        buffer.Append(text, s_start);

        var entries = buffer.Read(null, int.MaxValue);
        Assert.Equal(text, string.Concat(entries.Select(e => e.Text)));
        Assert.All(entries, e => Assert.True(e.Text.Length <= ConsoleBuffer.MaxEntryLength));
        Assert.All(entries, e => Assert.False(char.IsHighSurrogate(e.Text[^1])));
    }

    [Fact]
    public void AReadStopsAroundTheRequestedLengthButAlwaysReturnsSomething()
    {
        var buffer = new ConsoleBuffer(new FakeTimeProvider(s_start));
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(new string((char)('a' + i), 100), s_start.AddSeconds(i));
        }

        Assert.Equal(2, buffer.Read(null, 250).Count);
        Assert.Single(buffer.Read(null, 10));
    }

    [Fact]
    public void OutputThatLeftTheBufferIsReportedAsAGap()
    {
        var buffer = new ConsoleBuffer(new FakeTimeProvider(s_start), capacity: ConsoleBuffer.MaxEntryLength);
        buffer.Append(new string('a', 10_000), s_start);
        var first = buffer.Read(null, int.MaxValue)[0].Sequence;
        buffer.Append(new string('b', 10_000), s_start.AddSeconds(1));
        buffer.Append(new string('c', 10_000), s_start.AddSeconds(2)); // "a" and "b" go

        var entries = buffer.Read(first, int.MaxValue);

        Assert.True(entries[0].IsGap);
        Assert.Equal(first + 1, entries[0].Sequence); // everything up to "b" is missing
        Assert.Equal("", entries[0].Text);
        Assert.Equal('c', entries[1].Text[0]);
        Assert.False(buffer.Read(null, int.MaxValue)[0].IsGap); // a reader without history just gets what is there
    }

    [Fact]
    public async Task WaitingCompletesWhenNewOutputArrives()
    {
        var buffer = new ConsoleBuffer(new FakeTimeProvider(s_start));
        var nothingYet = buffer.WaitAsync(null, TestContext.Current.CancellationToken);
        Assert.False(nothingYet.IsCompleted);

        buffer.Append("hello\n", s_start);
        await nothingYet.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var last = buffer.Read(null, int.MaxValue)[^1].Sequence;
        var next = buffer.WaitAsync(last, TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);
        buffer.Append("again\n", s_start.AddMilliseconds(10));
        await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}

public sealed class ConsoleLogTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pulsar-console-log", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task LinesAreWrittenWithTheirTimeAndWithoutEscapeSequences()
    {
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 1, 500, TimeSpan.Zero);
        await using (var log = new ConsoleLog(_root, new ConsoleLogOptions(), TimeProvider.System, NullLogger.Instance))
        {
            log.Write("\u001b[32mLoading\u001b[0m map\r\nServer is ", at);
            log.Write("hibernating\r\nunfinished", at.AddSeconds(1));
        }

        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, ConsoleLog.FileName), TestContext.Current.CancellationToken);
        Assert.Equal(3, lines.Length);
        Assert.Equal("2026-10-02T12:00:01.500Z Loading map", lines[0]);
        Assert.Equal("2026-10-02T12:00:02.500Z Server is hibernating", lines[1]);
        Assert.EndsWith("Z unfinished", lines[2], StringComparison.Ordinal); // an unfinished last line is kept when closing
    }

    [Fact]
    public async Task FilesAreRotatedBySize()
    {
        var options = new ConsoleLogOptions { MaxFileBytes = 100, KeepFiles = 2, FlushInterval = TimeSpan.Zero };
        await using (var log = new ConsoleLog(_root, options, TimeProvider.System, NullLogger.Instance))
        {
            for (var i = 0; i < 20; i++)
            {
                log.Write($"line {i:00} {new string('x', 40)}\n", DateTimeOffset.UtcNow);
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        Assert.Equal(["console.1.log", "console.2.log", "console.log"], Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("line 19", await File.ReadAllTextAsync(Path.Combine(_root, "console.log"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }
}

/// <summary>A server's console under the supervisor: output, commands and usage (pseudo console and pipes).</summary>
public sealed class SupervisorConsoleTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeProcessManager _processes = new();
    private readonly RecordingEvents _events = new();
    private ServerSupervisor? _supervisor;

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }
    }

    [Fact]
    public async Task OutputReachesTheConsoleAcrossRestartsOfTheGame()
    {
        var supervisor = Supervisor(ManifestConsoleMode.Pty);
        await RunningAsync(supervisor);
        _processes.Last.Print("\u001b[33mhello\u001b[0m\r\n");
        await EventuallyAsync(() => Text(supervisor).Contains("hello", StringComparison.Ordinal), () => Text(supervisor));

        _processes.Last.Exit(3);
        await _events.WaitForAsync(RuntimeState.Crashed);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _events.WaitForAsync(RuntimeState.Starting, StateChangeReason.AutoRestart);
        await supervisor.FlushAsync();
        _processes.Last.Print("second run\r\n");

        await EventuallyAsync(() => Text(supervisor).Contains("second run", StringComparison.Ordinal), () => Text(supervisor));
        Assert.StartsWith("Test server ready\r\n\u001b[33mhello", Text(supervisor), StringComparison.Ordinal); // VT sequences kept for xterm
    }

    [Fact]
    public async Task APseudoConsoleGetsTheCommandWithItsNewlineAndEchoesItItself()
    {
        var supervisor = Supervisor(ManifestConsoleMode.Pty);
        await RunningAsync(supervisor);

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("say hi"));

        Assert.Equal("say hi\r\n", _processes.Last.Input[^1]);
        Assert.DoesNotContain("> say hi", Text(supervisor), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithPipesTheConsoleShowsTheCommand()
    {
        var supervisor = Supervisor(ManifestConsoleMode.Pipes);
        await RunningAsync(supervisor);

        Assert.Equal(AckStatus.Accepted, await supervisor.SendInputAsync("status"));

        Assert.Equal("status\n", _processes.Last.Input[^1]);
        Assert.EndsWith("> status\n", Text(supervisor), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("say \u001b[31mred")]
    [InlineData("two\nlines")]
    public async Task InvalidCommandsAreRejected(string line)
    {
        var supervisor = Supervisor(ManifestConsoleMode.Pty);
        await RunningAsync(supervisor);

        Assert.Equal(AckStatus.Rejected, await supervisor.SendInputAsync(line));
    }

    [Fact]
    public async Task CommandsAreRejectedWhenTheGameIsNotRunning()
    {
        var supervisor = Supervisor(ManifestConsoleMode.Pty);

        Assert.Equal(AckStatus.Rejected, await supervisor.SendInputAsync("status"));
    }

    [Fact]
    public async Task UsageIsTheShareOfTheMachineSinceThePreviousSample()
    {
        var supervisor = Supervisor(ManifestConsoleMode.Pty);
        Assert.Null(await supervisor.SampleAsync());
        await RunningAsync(supervisor);
        var process = _processes.Last;
        process.Usage = new ProcessUsage(TimeSpan.FromSeconds(1), 300 * 1024 * 1024, 2);

        var first = await supervisor.SampleAsync();
        Assert.Equal(0, first!.CpuPercent); // nothing to compare with yet
        Assert.Equal(300 * 1024 * 1024, first.MemoryBytes);
        Assert.Equal(2, first.ProcessCount);

        _clock.Advance(TimeSpan.FromSeconds(5));
        process.Usage = process.Usage with { CpuTime = TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(Environment.ProcessorCount * 5 / 4.0) };
        var second = await supervisor.SampleAsync();

        Assert.Equal(25, second!.CpuPercent, precision: 1); // a quarter of every core for 5 s
        Assert.InRange(second.Uptime, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));
    }

    private static string Text(ServerSupervisor supervisor) => string.Concat(supervisor.Console.Buffer.Read(null, int.MaxValue).Select(e => e.Text));

    private async Task RunningAsync(ServerSupervisor supervisor)
    {
        await supervisor.StartAsync();
        await _events.WaitForAsync(RuntimeState.Starting);
        await supervisor.FlushAsync();
        _processes.Last.Print("Test server ready\r\n");
        await _events.WaitForAsync(RuntimeState.Running, StateChangeReason.Ready);
    }

    private ServerSupervisor Supervisor(ManifestConsoleMode mode)
    {
        var plan = new LaunchPlan("bin/game.exe", "", [], new Dictionary<string, string>(),
            new ConsoleDefinition(mode, InputNewline: mode == ManifestConsoleMode.Pty ? "\r\n" : null),
            new LogPatternReadiness("^Test server ready", 60),
            new StopDefinition([new ConsoleCommandStop("quit")], 15));
        var definition = new ServerDefinition(Guid.NewGuid(), "abcd1234", @"C:\servers\abcd1234", ProcessIdentity.CurrentUser, plan,
            AutoStart: false, RestartOnCrash: true);
        _supervisor = new ServerSupervisor(definition, _processes, _events, _clock, new SupervisorOptions(), NullLogger.Instance);
        return _supervisor;
    }

    internal static async Task EventuallyAsync(Func<bool> condition, Func<string> what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what());
            await Task.Delay(20);
        }
    }
}
