using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>
/// Games in host processes on Linux (docs/architecture/06, "Server hosts"), for real: each host in a systemd unit of its
/// own, its game in a cgroup under it, and an agent that lets go of it and finds it again. Needs root and systemd.
/// </summary>
[Collection(RootTests.Name)]
public sealed class GameHostTests : IAsyncLifetime
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    private readonly string _data = LinuxOnly.NewServersFolder(); // the hosts write their logs there, out of their own /tmp
    private readonly string _servers = Path.Combine(LinuxOnly.NewServersFolder(), "servers");
    private readonly LinuxServerAccounts _accounts = new(NullLogger<LinuxServerAccounts>.Instance);
    private readonly List<string> _shortIds = [];
    private readonly List<GameHostReference> _hosts = [];

    private LinuxServerProvisioner Provisioner => new(_accounts, development: false, NullLogger<LinuxServerProvisioner>.Instance);

    public ValueTask InitializeAsync()
    {
        GameHostPipes.SocketDirectory = LinuxGameHostLauncher.RuntimeDirectory;
        Directory.CreateDirectory(LinuxGameHostLauncher.RuntimeDirectory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            Launcher().Kill(host);
        }

        foreach (var shortId in _shortIds)
        {
            Provisioner.Deprovision(_servers, shortId);
        }

        GameHostPipes.SocketDirectory = null;
        foreach (var folder in (string[])[Path.GetDirectoryName(_servers)!, _data])
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        return ValueTask.CompletedTask;
    }

    [LinuxRootFact]
    public async Task AGameRunsInAHostOfItsOwnUnitAndOutlivesTheAgent()
    {
        var server = Provision();
        var spec = Spec(server, ["--ready-after", "0"]);

        var game = (IHostedGameProcess)await Manager().StartAsync(spec, TestContext.Current.CancellationToken);
        _hosts.Add(game.Host);
        await WaitForAsync(game, "Test server ready");

        // A unit of its own, outside the tests' (as outside the agent's service).
        Assert.Equal("active", SystemdUnits.Systemctl("show", LinuxGameHostLauncher.UnitOf(spec.ServerId), "-p", "ActiveState", "--value").Trim());
        Assert.Contains($"pulsar-host-{spec.ServerId:N}.service", File.ReadAllText($"/proc/{game.Host.ProcessId}/cgroup"), StringComparison.Ordinal);
        // Its game in a cgroup under it.
        Assert.Contains($"game-{spec.ServerId:N}", File.ReadAllText($"/proc/{game.ProcessId}/cgroup"), StringComparison.Ordinal);

        // Other users' processes are hidden from it (their command lines hold other servers' passwords): root's first one too.
        await game.WriteInputAsync("read /proc/1/cmdline\n", TestContext.Current.CancellationToken);
        await WaitForAsync(game, "DENIED");

        // The agent goes; the game runs on, and the next agent finds it with what it printed meanwhile.
        await game.DetachAsync();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var again = await Manager().ReattachAsync(game.Host, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        await again.WriteInputAsync("say still here\n", TestContext.Current.CancellationToken);
        await WaitForAsync(again, "still here");

        await again.WriteInputAsync("quit\n", TestContext.Current.CancellationToken);
        Assert.Equal(0, await again.Exited.WaitAsync(s_wait, TestContext.Current.CancellationToken));
        await again.DisposeAsync();
    }

    [LinuxRootFact]
    public async Task AGamesCapsAreItsCgroups()
    {
        var server = Provision();
        var spec = Spec(server, ["--ready-after", "0"]) with { Limits = new ProcessLimits(50, 512L * 1024 * 1024) };

        await using var game = (IHostedGameProcess)await Manager().StartAsync(spec, TestContext.Current.CancellationToken);
        _hosts.Add(game.Host);
        await WaitForAsync(game, "Test server ready");

        var cgroup = GameCgroup(game.ProcessId);
        Assert.Equal((512L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(Path.Combine(cgroup, "memory.max")).Trim());
        Assert.Equal($"{GameCgroups.CpuPeriodMicroseconds * Environment.ProcessorCount / 2} {GameCgroups.CpuPeriodMicroseconds}",
            File.ReadAllText(Path.Combine(cgroup, "cpu.max")).Trim());

        await game.ApplyLimitsAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("max", File.ReadAllText(Path.Combine(cgroup, "memory.max")).Trim());

        var usage = await game.SampleUsageAsync(TestContext.Current.CancellationToken);
        Assert.True(usage.MemoryBytes > 0);
        Assert.Equal(1, usage.ProcessCount);
    }

    [LinuxRootFact]
    public async Task KillingTheHostKillsItsGame()
    {
        var server = Provision();
        var spec = Spec(server, ["--ready-after", "0"]);
        var game = (IHostedGameProcess)await Manager().StartAsync(spec, TestContext.Current.CancellationToken);
        await WaitForAsync(game, "Test server ready");
        var gameProcess = game.ProcessId;

        Assert.True(Launcher().Kill(game.Host));

        Assert.False(Launcher().IsAlive(game.Host));
        var deadline = DateTime.UtcNow + s_wait;
        while (ProcFs.Stat(gameProcess) is { State: not 'Z' })
        {
            Assert.True(DateTime.UtcNow < deadline, "the game outlived its host");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await game.DisposeAsync();
    }

    [LinuxRootFact]
    public void AHostIsKnownByItsStartTooNotItsIdAlone()
    {
        var self = Environment.ProcessId;
        var started = ProcFs.StartTime(self)!.Value;

        Assert.True(Launcher().IsAlive(new GameHostReference("pulsar-host-x", self, started)));
        Assert.False(Launcher().IsAlive(new GameHostReference("pulsar-host-x", self, started.AddTicks(-1))));
    }

    private LinuxGameHostLauncher Launcher() => new(() => TestGame.Agent, _data, null, NullLogger<LinuxGameHostLauncher>.Instance)
    {
        // The tests' agent build is framework-dependent: its hosts find the .NET the tests run on.
        Environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = TestGame.DotnetRoot },
    };

    private GameHostManager Manager() =>
        new(new LinuxGameProcessManager(_accounts, TestGame.Agent, TimeProvider.System, NullLogger<LinuxGameProcessManager>.Instance), Launcher(),
            NullLogger<GameHostManager>.Instance);

    private ProvisionedServer Provision()
    {
        var shortId = "lh" + Guid.NewGuid().ToString("N")[..8];
        _shortIds.Add(shortId);
        var server = Provisioner.Provision(_servers, shortId);
        var account = _accounts.Lookup(LinuxServerAccounts.AccountName(shortId))!;
        var bin = Path.Combine(server.Directory, "bin");
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.EnumerateFiles(TestGame.Directory))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }

        File.SetUnixFileMode(Path.Combine(bin, "Pulsar.TestGameServer"), (UnixFileMode)0x1ED);
        SystemdUnits.Run("chown", ["-R", $"{account.Uid}:{account.Gid}", bin]);
        return server;
    }

    private static ProcessLaunchSpec Spec(ProvisionedServer server, IReadOnlyList<string> arguments) =>
        new(Guid.NewGuid(), server.Directory, "bin/Pulsar.TestGameServer", arguments, "",
            new Dictionary<string, string> { ["DOTNET_ROOT"] = TestGame.DotnetRoot }, ConsoleMode.Pipes, ConsoleEncoding.Utf8, server.Identity);

    private static string GameCgroup(int pid) =>
        "/sys/fs/cgroup" + File.ReadLines($"/proc/{pid}/cgroup").First(l => l.StartsWith("0::", StringComparison.Ordinal))[3..].Trim();

    private static async Task WaitForAsync(IGameProcess game, string text)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(s_wait);
        var seen = new StringBuilder();
        while (!seen.ToString().Contains(text, StringComparison.Ordinal))
        {
            try
            {
                seen.Append((await game.Output.ReadAsync(timeout.Token)).Text);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
            {
                Assert.Fail($"'{text}' did not show; the game printed: {seen}");
            }
        }
    }
}
