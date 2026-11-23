using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>
/// Games on Linux, started for real: through <c>PulsarAgent game-exec</c> (the agent's Linux build, beside the tests) as
/// a server's own user, with the test game. Needs root (it makes users), as the agent has.
/// </summary>
[Collection(RootTests.Name)]
public sealed class GameProcessTests : IAsyncLifetime
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    private readonly string _servers = Path.Combine(LinuxOnly.NewFolder(), "servers");
    private readonly LinuxServerAccounts _accounts = new(NullLogger<LinuxServerAccounts>.Instance);
    private readonly List<string> _shortIds = [];
    private LinuxServerProvisioner Provisioner => new(_accounts, development: false, NullLogger<LinuxServerProvisioner>.Instance);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var shortId in _shortIds)
        {
            Provisioner.Deprovision(_servers, shortId);
        }

        return ValueTask.CompletedTask;
    }

    [LinuxRootFact]
    public async Task AGameRunsAsItsServersUserWithItsPipes()
    {
        var (server, account) = Provision();
        await using var game = await StartAsync(server, ["--ready-after", "0"], new Dictionary<string, string> { ["PULSAR_TEST_GREETING"] = "hola" });

        await game.WaitForAsync("Test server ready");
        await game.Process.WriteInputAsync("whoami\n", TestContext.Current.CancellationToken);
        await game.WaitForAsync($"user={account.Name}");
        await game.Process.WriteInputAsync("env PULSAR_TEST_GREETING\n", TestContext.Current.CancellationToken);
        await game.WaitForAsync("PULSAR_TEST_GREETING=hola");
        // Nothing of root's environment, its own home and temporary folder (made as its user).
        await game.Process.WriteInputAsync("env HOME\n", TestContext.Current.CancellationToken);
        await game.WaitForAsync($"HOME={server.Directory}");
        await game.Process.WriteInputAsync("env SUDO_USER\n", TestContext.Current.CancellationToken);
        await game.WaitForAsync("SUDO_USER=" + Environment.NewLine);
        Assert.Equal(account.Uid, Libc.Lstat(Path.Combine(server.Directory, ".pulsar", "temp"))!.Value.Uid);

        await game.Process.WriteInputAsync("quit\n", TestContext.Current.CancellationToken);
        Assert.Equal(0, await game.Process.Exited.WaitAsync(s_wait, TestContext.Current.CancellationToken));
    }

    [LinuxRootFact]
    public async Task AGamesUserCannotReadRootsFilesNorTakeRootBack()
    {
        var (server, _) = Provision();
        await using var game = await StartAsync(server, ["--ready-after", "0"]);
        await game.WaitForAsync("Test server ready");

        await game.Process.WriteInputAsync("read /etc/shadow\n", TestContext.Current.CancellationToken);
        await game.WaitForAsync("DENIED");
        Assert.Equal("1", NoNewPrivileges(game.Process.ProcessId));
    }

    [LinuxRootFact]
    public async Task KillingTheTreeKillsWhatTheGameStarted()
    {
        var (server, _) = Provision();
        await using var game = await StartAsync(server, ["--ready-after", "0"]);
        await game.WaitForAsync("Test server ready");
        await game.Process.WriteInputAsync("spawn\n", TestContext.Current.CancellationToken);
        var line = await game.WaitForAsync("child ");
        var child = int.Parse(line[(line.IndexOf("child ", StringComparison.Ordinal) + 6)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        // The game, its child and the child's own child.
        await Eventually(() => game.Process.CountProcesses() == 3);

        game.Process.TerminateTree(1);

        await game.Process.Exited.WaitAsync(s_wait, TestContext.Current.CancellationToken);
        await Eventually(() => ProcFs.Stat(child) is null or { State: 'Z' });
    }

    [LinuxRootFact]
    public async Task CtrlCIsSigintToTheGame()
    {
        var (server, _) = Provision();
        await using var game = await StartAsync(server, ["--ready-after", "0"]);
        await game.WaitForAsync("Test server ready");

        Assert.True(game.Process.SupportsInterrupt);
        await game.Process.SendInterruptAsync(TestContext.Current.CancellationToken);

        await game.WaitForAsync("interrupted");
    }

    [LinuxRootFact]
    public async Task UsageIsTheTreesOwn()
    {
        var (server, _) = Provision();
        await using var game = await StartAsync(server, ["--ready-after", "0"]);
        await game.WaitForAsync("Test server ready");

        var usage = game.Process.SampleUsage();

        Assert.True(usage.MemoryBytes > 1024 * 1024, $"{usage.MemoryBytes} bytes");
        Assert.Equal(1, usage.ProcessCount);
    }

    [LinuxRootFact]
    public async Task AScriptIsNotStarted()
    {
        var (server, _) = Provision();
        var script = Path.Combine(server.Directory, "bin", "start");
        File.WriteAllText(script, "#!/bin/sh\necho started by a script\n");
        File.SetUnixFileMode(script, (UnixFileMode)0x1ED); // 0755

        await using var game = await StartAsync(server, [], executable: "bin/start");

        Assert.Equal(GameExec.CannotRun, await game.Process.Exited.WaitAsync(s_wait, TestContext.Current.CancellationToken));
        Assert.Contains("is not a program", await game.AllOutputAsync(), StringComparison.Ordinal);
    }

    [LinuxRootFact]
    public async Task AnExecutableThatIsALinkIsRefused()
    {
        var (server, _) = Provision();
        File.CreateSymbolicLink(Path.Combine(server.Directory, "bin", "sh"), "/bin/sh");

        var refused = await Assert.ThrowsAsync<GameProcessException>(() => StartAsync(server, [], executable: "bin/sh"));
        Assert.Equal("process.path_outside_server", refused.Code);
    }

    [LinuxRootFact]
    public async Task AServerWithoutItsOwnAccountIsRefusedOutsideDevelopment()
    {
        var (server, _) = Provision();
        var manager = Manager();

        var refused = await Assert.ThrowsAsync<GameProcessException>(() => manager.StartAsync(Spec(server with { Identity = ProcessIdentity.CurrentUser }, []),
            TestContext.Current.CancellationToken));

        Assert.Equal("process.identity_refused", refused.Code);
    }

    [LinuxFact]
    public void GameExecNeverRunsAGameAsRoot()
    {
        Assert.False(GameExec.TryParse(["game-exec", "--uid", "0", "--gid", "1000", "--env-file", "/run/env", "--cwd", "/tmp", "--", "/bin/true"], out _, out var usage));
        Assert.Contains("root", usage, StringComparison.Ordinal);
        Assert.False(GameExec.TryParse(["game-exec", "--uid", "1000", "--gid", "0", "--env-file", "/run/env", "--cwd", "/tmp", "--", "/bin/true"], out _, out _));
        Assert.False(GameExec.TryParse(["game-exec", "--uid", "1000", "--gid", "1000", "--env-file", "/run/env", "--cwd", "relative", "--", "/bin/true"], out _, out _));

        Assert.True(GameExec.TryParse(["game-exec", "--uid", "1000", "--gid", "1001", "--env-file", "/run/env", "--mkdir", "/srv/a", "--cwd", "/srv", "--", "/srv/game", "-x", "--y"],
            out var request, out _));
        Assert.Equal((1000u, 1001u, "/srv", "/srv/game"), (request.Uid, request.Gid, request.WorkingDirectory, request.Executable));
        Assert.Equal(["-x", "--y"], request.Arguments);
        Assert.Equal(["/srv/a"], request.Directories);
    }

    [LinuxRootFact]
    public void AServersFolderIsItsUsersAndClosedToOthers()
    {
        var (server, account) = Provision();

        var stat = Libc.Lstat(server.Directory)!.Value;
        Assert.Equal((account.Uid, account.Gid, 0x1E8u), (stat.Uid, stat.Gid, stat.Permissions));
        Assert.Equal(0x1C9u, Libc.Lstat(_servers)!.Value.Permissions);
        Assert.Equal(server.Directory, account.Home);
    }

    [LinuxRootFact]
    public void DeletingAServerNeverFollowsItsLinks()
    {
        var (server, _) = Provision();
        var outside = LinuxOnly.NewFolder();
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not the server's");
        Directory.CreateSymbolicLink(Path.Combine(server.Directory, "elsewhere"), outside);
        var shortId = Path.GetFileName(server.Directory);

        Provisioner.Deprovision(_servers, shortId);
        _shortIds.Remove(shortId);

        Assert.False(Directory.Exists(server.Directory));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.Null(_accounts.Lookup(LinuxServerAccounts.AccountName(shortId)));
    }

    [LinuxRootFact]
    public void AnAccountTheAgentDidNotMakeIsNotTakenOver()
    {
        var shortId = NewShortId();
        var name = LinuxServerAccounts.AccountName(shortId);
        SystemdUnits.Run("useradd", ["--system", "--no-create-home", "--shell", "/usr/sbin/nologin", name]);
        try
        {
            var refused = Assert.Throws<GameProcessException>(() => Provisioner.Provision(_servers, shortId));
            Assert.Equal("server.account_unusable", refused.Code);
            Assert.Throws<InvalidOperationException>(() => _accounts.Delete(name));
        }
        finally
        {
            SystemdUnits.Run("userdel", [name]);
        }
    }

    private (ProvisionedServer Server, LinuxAccount Account) Provision()
    {
        var shortId = NewShortId();
        _shortIds.Add(shortId);
        var server = Provisioner.Provision(_servers, shortId);
        var account = _accounts.Lookup(LinuxServerAccounts.AccountName(shortId))!;
        // The test game, copied in as an install would, then made the server's user's.
        var bin = Path.Combine(server.Directory, "bin");
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.EnumerateFiles(TestGame.Directory))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }

        File.SetUnixFileMode(Path.Combine(bin, "Pulsar.TestGameServer"), (UnixFileMode)0x1ED);
        SystemdUnits.Run("chown", ["-R", $"{account.Uid}:{account.Gid}", bin]);
        return (server, account);
    }

    private static string NewShortId() => "lx" + Guid.NewGuid().ToString("N")[..8];

    private static LinuxGameProcessManager Manager() =>
        new(new LinuxServerAccounts(NullLogger<LinuxServerAccounts>.Instance), TestGame.Agent, TimeProvider.System, NullLogger<LinuxGameProcessManager>.Instance);

    private static ProcessLaunchSpec Spec(ProvisionedServer server, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null,
        string executable = "bin/Pulsar.TestGameServer")
    {
        // The tests' builds of the agent and the test game need the .NET the tests run on (a release's agent is self-contained).
        var variables = new Dictionary<string, string>(environment ?? new Dictionary<string, string>()) { ["DOTNET_ROOT"] = TestGame.DotnetRoot };
        return new(Guid.NewGuid(), server.Directory, executable, arguments, "", variables, ConsoleMode.Pipes, ConsoleEncoding.Utf8, server.Identity);
    }

    private static async Task<RunningGame> StartAsync(ProvisionedServer server, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null, string executable = "bin/Pulsar.TestGameServer") =>
        new(await Manager().StartAsync(Spec(server, arguments, environment, executable), TestContext.Current.CancellationToken));

    private static string NoNewPrivileges(int pid) =>
        File.ReadLines($"/proc/{pid}/status").First(l => l.StartsWith("NoNewPrivs:", StringComparison.Ordinal))["NoNewPrivs:".Length..].Trim();

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A started game and what it printed so far.</summary>
    private sealed class RunningGame(IGameProcess process) : IAsyncDisposable
    {
        private readonly StringBuilder _seen = new();

        public IGameProcess Process { get; } = process;

        /// <summary>Reads the output until <paramref name="text"/> shows; returns the line it is in.</summary>
        public async Task<string> WaitForAsync(string text)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(s_wait);
            while (true)
            {
                var all = _seen.ToString();
                var at = all.IndexOf(text, StringComparison.Ordinal);
                if (at >= 0)
                {
                    var start = all.LastIndexOf('\n', at) + 1;
                    var end = all.IndexOf('\n', at);
                    _seen.Remove(0, end < 0 ? all.Length : end + 1);
                    return all[start..(end < 0 ? all.Length : end)];
                }

                try
                {
                    _seen.Append((await Process.Output.ReadAsync(timeout.Token)).Text.Replace("\r\n", "\n", StringComparison.Ordinal));
                }
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                {
                    Assert.Fail($"'{text}' did not show; the game printed: {all}");
                }
            }
        }

        public async Task<string> AllOutputAsync()
        {
            await foreach (var chunk in Process.Output.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                _seen.Append(chunk.Text);
            }

            return _seen.ToString();
        }

        public ValueTask DisposeAsync() => Process.DisposeAsync();
    }
}
