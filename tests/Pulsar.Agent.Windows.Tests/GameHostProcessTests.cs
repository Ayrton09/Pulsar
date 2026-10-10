using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Processes;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Games in real host processes (<c>PulsarAgent host</c>, docs/architecture/06, "Server hosts"): the test plays the
/// agent, whose connection ends without stopping the game, and comes back for it.
/// </summary>
public sealed class GameHostProcessTests : IAsyncDisposable
{
    private readonly TestServerDirectory _server = new();
    private readonly string _data = Directory.CreateTempSubdirectory("pulsar-host-data-").FullName;
    private readonly WindowsGameHostLauncher _launcher;
    private readonly GameHostManager _hosts;
    private readonly List<GameHostReference> _started = [];

    public GameHostProcessTests()
    {
        _launcher = new WindowsGameHostLauncher(Path.Combine(AppContext.BaseDirectory, "agent", "PulsarAgent.exe"), _data, null,
            NullLogger<WindowsGameHostLauncher>.Instance);
        _hosts = new GameHostManager(TestServerDirectory.Manager(), _launcher, NullLogger<GameHostManager>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var host in _started)
        {
            _launcher.Kill(host);
        }

        _server.Dispose();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // A host's log file closes a moment after it ends.
        }

        return ValueTask.CompletedTask;
    }

    [WindowsFact]
    public async Task AGameOutlivesTheAgentsConnectionAndTheNextAgentContinuesItsConsole()
    {
        var process = await StartAsync();
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");
        var gameId = process.ProcessId;

        await process.DetachAsync();
        await output.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(_launcher.IsAlive(process.Host));
        using (var game = Process.GetProcessById(gameId))
        {
            Assert.False(game.HasExited);
        }

        var again = await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        Assert.Equal(gameId, again.ProcessId);
        var resumed = new OutputCollector(again);
        await again.WriteInputAsync("say still here\r\n", TestContext.Current.CancellationToken);
        await resumed.WaitForAsync("still here");
        Assert.Contains("Test server ready", resumed.Text, StringComparison.Ordinal); // asked from 0: everything the host kept

        await again.WriteInputAsync("quit\r\n", TestContext.Current.CancellationToken);
        Assert.Equal(0, await again.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await again.DisposeAsync();
        await WaitUntilAsync(() => !_launcher.IsAlive(process.Host));
    }

    [WindowsFact]
    public async Task KillingTheHostKillsItsGameTree()
    {
        var process = await StartAsync();
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");
        await process.WriteInputAsync("spawn\r\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("grandchild");
        Assert.Equal(3, process.CountProcesses()); // the game, its child and its grandchild (the console host is not in the job)
        var gameId = process.ProcessId;

        _launcher.Kill(process.Host);

        Assert.Equal(HostedGameProcess.HostLostExitCode, await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => !ProcessRuns(gameId));
        Assert.Null(await _hosts.ReattachAsync(process.Host, 0, TestContext.Current.CancellationToken));
    }

    [WindowsFact]
    public async Task APipeIsTrustedOnlyFromItsOwnHostProcess()
    {
        var process = await StartAsync();
        var impostor = process.Host with { ProcessId = Environment.ProcessId };
        await process.DetachAsync();

        await using var pipe = GameHostPipes.CreateClient(process.Host.Pipe);
        await pipe.ConnectAsync(30_000, TestContext.Current.CancellationToken);
        Assert.True(_launcher.Serves(pipe, process.Host));
        Assert.False(_launcher.Serves(pipe, impostor));
        Assert.False(_launcher.IsAlive(process.Host with { ProcessStartedAt = process.Host.ProcessStartedAt.AddSeconds(-1) })); // a reused id
    }

    private async Task<IHostedGameProcess> StartAsync()
    {
        var process = (IHostedGameProcess)await _hosts.StartAsync(_server.Spec(ConsoleMode.Pty, "--ready-after", "100"), TestContext.Current.CancellationToken);
        _started.Add(process.Host);
        return process;
    }

    private static bool ProcessRuns(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
