using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;

namespace Pulsar.Agent.Core.Tests;

public sealed class SteamCmdOutputTests
{
    [Theory]
    [InlineData(" Update state (0x61) downloading, progress: 9.44 (238319757 / 2524405728)", "downloading", 9.44)]
    [InlineData(" Update state (0x81) verifying update, progress: 100.00 (74000614728 / 74000614728)", "verifying", 100)]
    [InlineData(" Update state (0x101) committing, progress: 50.00 (5 / 10)", "committing", 50)]
    [InlineData(" Update state (0x3) reconfiguring, progress: 0.00 (0 / 0)", "installing", 0)]
    public void ReadsProgressLines(string line, string stage, double percent)
    {
        var progress = Assert.IsType<SteamCmdLine.Progress>(SteamCmdOutput.Parse(line));

        Assert.Equal(stage, progress.Stage);
        Assert.Equal(percent, progress.Percent, 2);
    }

    [Fact]
    public void ReadsResultsAndIgnoresTranslatedChatter()
    {
        Assert.IsType<SteamCmdLine.Success>(SteamCmdOutput.Parse("Success! App '730' fully installed."));
        Assert.IsType<SteamCmdLine.Success>(SteamCmdOutput.Parse("Success! App '232330' already up to date."));
        Assert.Equal("ERROR! Failed to install app '232330' (Missing configuration)",
            Assert.IsType<SteamCmdLine.Failure>(SteamCmdOutput.Parse("ERROR! Failed to install app '232330' (Missing configuration)")).Message);
        Assert.IsType<SteamCmdLine.Failure>(SteamCmdOutput.Parse("Error! App '730' state is 0x202 after update job."));
        Assert.IsType<SteamCmdLine.SelfUpdate>(SteamCmdOutput.Parse("[  0%] Buscando actualizaciones disponibles..."));
        Assert.IsType<SteamCmdLine.SelfUpdate>(SteamCmdOutput.Parse("[----] Verificando instalación..."));
        Assert.Null(SteamCmdOutput.Parse("Connecting anonymously to Steam Public...OK"));
        Assert.Null(SteamCmdOutput.Parse("Esperando la información del usuario...OK"));
    }

    [Theory]
    [InlineData("ERROR! Failed to install app '232330' (Missing configuration)", "steamcmd.failed", true)]
    [InlineData("ERROR! Timed out waiting for AppInfo update.", "steamcmd.failed", true)]
    [InlineData("ERROR! Failed to install app '730' (Disk write failure)", "steamcmd.disk", false)]
    [InlineData("Error! App '730' state is 0x202 after update job.", "steamcmd.disk", false)]
    [InlineData("ERROR! Failed to install app '730' (No subscription)", "steamcmd.app_unavailable", false)]
    [InlineData(null, "steamcmd.failed", true)]
    public void ClassifiesFailures(string? error, string code, bool retry)
    {
        Assert.Equal((code, retry), SteamCmdOutput.Classify(error));
    }
}

public sealed class SteamCmdRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-steamcmd-runner", Guid.NewGuid().ToString("N"));
    private readonly FakeProcessManager _processes = new();
    private readonly FakeSource _source;
    private readonly List<(string Stage, double? Percent)> _progress = [];

    public SteamCmdRunnerTests()
    {
        Directory.CreateDirectory(_root);
        _source = new FakeSource(Path.Combine(_root, "bootstrapper.exe"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task InstallsAndReportsProgress()
    {
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Connecting anonymously to Steam Public...OK\r\n");
            process.Print(" Update state (0x61) downloading, progress: 10.00 (10 / 100)\r\n Update state (0x61) downloading, progress: 10.20 (102 / 1000)\r\n");
            process.Print(" Update state (0x61) downloading, progress: 60.00 (60 / 100)\r\n Update state (0x81) verifying update, progress: 100.00 (100 / 100)\r\n");
            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            ("queued", null), ("preparing", null), ("downloading", 10), ("downloading", 60), ("verifying", 100),
        ], _progress);
        var spec = Assert.Single(_processes.Specs);
        Assert.Equal(SteamCmdRunner.Executable, spec.Executable);
        Assert.Equal(ConsoleMode.Pty, spec.ConsoleMode);
        Assert.Equal(ProcessIdentity.Account("pulsar_abcd2345"), spec.Identity);
        Assert.Equal("bootstrapper", File.ReadAllText(Path.Combine(_root, "server", ".pulsar", "steamcmd", "steamcmd.exe")));
    }

    [Fact]
    public void TheCommandLineSetsTheDirectoryBeforeLoggingIn()
    {
        var arguments = SteamCmdRunner.Arguments(Job() with { Beta = "public_beta", BetaPassword = "pw", Validate = true });

        Assert.Equal(
        [
            "+@ShutdownOnFailedCommand", "1", "+@NoPromptForPassword", "1", "+force_install_dir", Path.Combine(_root, "server"), "+login", "anonymous",
            "+app_update", "232330", "-beta", "public_beta", "-betapassword", "pw", "validate", "+quit",
        ], arguments);
        Assert.DoesNotContain("pw", Job().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelfUpdateRunsSteamCmdAgainWithoutCountingAsAFailure()
    {
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 1)
            {
                process.Print("[ 50%] Downloading update (1,000 of 2,000 KB)...\r\n");
                process.Exit(7);
                return;
            }

            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal(2, _processes.Started.Count);
        Assert.Contains(("preparing", null), _progress);
    }

    [Fact]
    public async Task TransientFailuresAreRetried()
    {
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 1)
            {
                process.Print("ERROR! Failed to install app '232330' (Missing configuration)\r\n");
                process.Exit(8);
                return;
            }

            process.Print("Success! App '232330' fully installed.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal(2, _processes.Started.Count);
        Assert.Contains(("retrying", null), _progress);
    }

    [Fact]
    public async Task AttemptsAreBounded()
    {
        _processes.OnStarted = (process, _) => process.Exit(8);
        using var runner = Runner();

        var error = await Assert.ThrowsAsync<GameProcessException>(() => runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.failed", error.Code);
        Assert.Equal(3, _processes.Started.Count);
        Assert.Contains("exited with code 8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullDiskIsNotRetried()
    {
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Error! App '730' state is 0x202 after update job.\r\n");
            process.Exit(8);
        };
        using var runner = Runner();

        var error = await Assert.ThrowsAsync<GameProcessException>(() => runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken));

        Assert.Equal("steamcmd.disk", error.Code);
        Assert.Single(_processes.Started);
    }

    [Fact]
    public async Task CancellingKillsSteamCmd()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _processes.OnStarted = (process, _) =>
        {
            process.Print(" Update state (0x61) downloading, progress: 1.00 (1 / 100)\r\n");
            cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        };
        using var runner = Runner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Job(), Report, cancel.Token));

        Assert.Equal(1, Assert.Single(_processes.Started).TerminatedWith);
    }

    [Fact]
    public async Task AStuckRunIsKilledAndRetried()
    {
        var clock = new FakeTimeProvider();
        _processes.OnStarted = (process, _) =>
        {
            if (_processes.Started.Count == 2)
            {
                process.Print("Success! App '232330' fully installed.\r\n");
                process.Exit(0);
            }
        };
        using var runner = Runner(clock);

        var run = runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);
        while (_processes.Started.Count == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        clock.Advance(TimeSpan.FromMinutes(11));
        while (_processes.Started.Count < 2)
        {
            clock.Advance(TimeSpan.FromSeconds(10)); // the retry delay
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await run;
        Assert.Equal(1, _processes.Started[0].TerminatedWith);
    }

    [Fact]
    public async Task TheServersCopyIsKeptOnceItExists()
    {
        var copy = Path.Combine(_root, "server", ".pulsar", "steamcmd", "steamcmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "updated by itself");
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Success! App '232330' already up to date.\r\n");
            process.Exit(0);
        };
        using var runner = Runner();

        await runner.RunAsync(Job(), Report, TestContext.Current.CancellationToken);

        Assert.Equal("updated by itself", File.ReadAllText(copy));
        Assert.Equal(0, _source.Calls);
    }

    private SteamCmdRunner Runner(TimeProvider? clock = null) => new(_processes, _source, new DirectServerFiles(),
        new SteamCmdOptions { RetryDelays = clock is null ? [TimeSpan.Zero] : [TimeSpan.FromSeconds(10)] }, clock ?? TimeProvider.System,
        NullLogger<SteamCmdRunner>.Instance);

    private SteamCmdJob Job() => new(Guid.NewGuid(), Path.Combine(_root, "server"), ProcessIdentity.Account("pulsar_abcd2345"), 232330, null, null, false);

    private Task Report(string stage, double? percent)
    {
        lock (_progress)
        {
            _progress.Add((stage, percent));
        }

        return Task.CompletedTask;
    }

    private sealed class FakeSource(string path) : ISteamCmdSource
    {
        public int Calls { get; private set; }

        public Task<string> GetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            File.WriteAllText(path, "bootstrapper");
            return Task.FromResult(path);
        }
    }
}
