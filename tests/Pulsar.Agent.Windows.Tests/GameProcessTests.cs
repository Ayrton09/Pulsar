using System.Diagnostics;
using System.IO.Pipes;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Windows.Processes;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// The Windows process manager against real processes (the test game server), running as the current user: Job
/// Objects, pseudo console and pipes, input, Ctrl+C, exit codes, handle inheritance and path checks. Launching as a
/// dedicated account needs SYSTEM and is checked in Windows Sandbox and CI (scripts/sandbox/agent-install).
/// </summary>
public sealed class GameProcessTests : IDisposable
{
    private readonly TestServerDirectory _server = new();

    public void Dispose() => _server.Dispose();

    [WindowsFact]
    public async Task APseudoConsoleCapturesEveryKindOfOutputAndTakesInput()
    {
        await using var process = await Start(ConsoleMode.Pty);
        var output = new OutputCollector(process);

        await output.WaitForAsync("Test server ready");
        await process.WriteInputAsync("say héllo ñandú\r\n", TestContext.Current.CancellationToken);
        await process.WriteInputAsync("conout through the console API\r\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("Test server (console): through the console API");
        await process.WriteInputAsync("quit\r\n", TestContext.Current.CancellationToken);

        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var text = output.Text;
        Assert.Contains("Test server: hello from stderr", text, StringComparison.Ordinal);
        Assert.Contains("Test server: héllo ñandú", text, StringComparison.Ordinal);
        Assert.True(process.SupportsInterrupt);
    }

    [WindowsFact]
    public async Task ServersRunAsTheAgentsOwnAccountOnlyInDevelopment()
    {
        // In production the agent is SYSTEM: a spec without a server account would run the game as SYSTEM. Fails closed.
        var production = new WindowsGameProcessManager(TestAccounts.Create(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsGameProcessManager>.Instance);

        var error = await Assert.ThrowsAsync<GameProcessException>(() => production.StartAsync(_server.Spec(ConsoleMode.Pipes), TestContext.Current.CancellationToken));

        Assert.Equal("process.identity_refused", error.Code);
        Assert.Contains("--dev", error.Message, StringComparison.Ordinal);
        await using var process = await Start(ConsoleMode.Pipes); // the tests' manager is a development agent's
        Assert.True(process.ProcessId > 0);
    }

    [WindowsFact]
    public async Task PipesCaptureStandardOutputAndErrorAndTakeInput()
    {
        await using var process = await Start(ConsoleMode.Pipes);
        var output = new OutputCollector(process);

        await output.WaitForAsync("Test server ready");
        await process.WriteInputAsync("say piped\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("Test server: piped");
        await process.WriteInputAsync("quit\n", TestContext.Current.CancellationToken);

        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await output.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("Test server: hello from stderr", output.Text, StringComparison.Ordinal);
        Assert.False(process.SupportsInterrupt); // only a host sends Ctrl+C to a game run with pipes (GameHostProcessTests)
    }

    [WindowsFact]
    public async Task EveryArgumentArrivesExactlyAsGiven()
    {
        string[] arguments = ["plain", "with space", "with \"quotes\"", @"trailing\", @"back\\slash""quote", "", "+map de_dust2", "tab\there"];
        await using var process = await Start(ConsoleMode.Pipes, ["--print-args", .. arguments]);
        var output = new OutputCollector(process);

        await output.WaitForAsync("Test server ready");
        foreach (var argument in arguments)
        {
            Assert.Contains($"arg[{argument}]", output.Text, StringComparison.Ordinal);
        }
    }

    [WindowsFact]
    public async Task ALineWiderThanThePseudoConsoleArrivesWhole()
    {
        // Once its screen is full, the console host cuts a line wider than it and writes the last cell again: the link
        // SCP: Secret Laboratory's "!verify static" prints came in two, with a character doubled.
        var cancellation = TestContext.Current.CancellationToken;
        await using var process = await Start(ConsoleMode.Pty);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");
        await process.WriteInputAsync("spam 60 40\r\n", cancellation);
        await output.WaitForAsync("Test server: spam 59 ");

        var link = "https://example.com/verify?" + string.Concat(Enumerable.Range(0, 480).Select(i => (char)('a' + (i % 26))));
        await process.WriteInputAsync($"color {link}\r\nspam 20 700\r\nsay done\r\n", cancellation);
        await output.WaitForAsync("Test server: done");

        var lines = new ConsoleLines().Add(output.Text);
        Assert.Contains($"Test server: {link}", lines);
        var spam = lines.Where(l => l.StartsWith("Test server: spam ", StringComparison.Ordinal) && l.Length > 100).ToList();
        Assert.Equal(20, spam.Count);
        Assert.All(spam, l => Assert.Matches("^Test server: spam [0-9]+ (abcdefghijklmnopqrstuvwxyz)+[a-z]* end$", l));
        Assert.All(spam, l => Assert.Equal(700, l.Length));
        Assert.DoesNotContain($";{PseudoConsoleWraps.Width}H", output.Text, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task CtrlCReachesTheProcessThroughThePseudoConsole()
    {
        await using var process = await Start(ConsoleMode.Pty);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        await process.SendInterruptAsync(TestContext.Current.CancellationToken);

        var code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(code == 130, $"Exit code {code}; output: {output.Text}");
    }

    [WindowsFact]
    public async Task UsageCoversTheWholeTree()
    {
        await using var process = await Start(ConsoleMode.Pipes);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        var alone = process.SampleUsage();
        await process.WriteInputAsync("spawn\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("Test server: grandchild");
        var withChildren = process.SampleUsage();

        Assert.InRange(alone.ProcessCount, 1, 2); // the game and, for a console program, its conhost.exe
        Assert.True(alone.MemoryBytes > 10 * 1024 * 1024, $"{alone.MemoryBytes} bytes"); // a .NET process
        Assert.True(alone.CpuTime > TimeSpan.Zero);
        Assert.Equal(alone.ProcessCount + 2, withChildren.ProcessCount); // a child and a grandchild
        Assert.True(withChildren.MemoryBytes > alone.MemoryBytes + (10 * 1024 * 1024), $"{withChildren.MemoryBytes} vs {alone.MemoryBytes}"); // two more .NET processes
        Assert.True(withChildren.CpuTime >= alone.CpuTime);

        process.TerminateTree(1);
        await process.DisposeAsync();
        Assert.Equal(new ProcessUsage(TimeSpan.Zero, 0, 0), process.SampleUsage());
    }

    [WindowsFact]
    public async Task AMemoryLimitRefusesMemoryAndSaysSo()
    {
        await using var process = await Start(ConsoleMode.Pipes, new ProcessLimits(null, 256L * 1024 * 1024));
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        await process.WriteInputAsync("allocate 64\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("Test server: allocated 64 MB");
        Assert.False(process.ReachedMemoryLimit);
        Assert.True(process.SampleUsage().CommittedBytes > 64L * 1024 * 1024);

        await process.WriteInputAsync("allocate 512\n", TestContext.Current.CancellationToken);

        var code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain("allocated 512", output.Text, StringComparison.Ordinal);
        await WaitUntilAsync(() => process.ReachedMemoryLimit);
    }

    [WindowsFact]
    public async Task AMemoryLimitAppliesToARunningProcess()
    {
        await using var process = await Start(ConsoleMode.Pipes);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");
        await process.WriteInputAsync("allocate 320\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("Test server: allocated 320 MB");

        process.ApplyLimits(new ProcessLimits(null, 512L * 1024 * 1024));
        await process.WriteInputAsync("allocate 320\n", TestContext.Current.CancellationToken);

        Assert.NotEqual(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => process.ReachedMemoryLimit);
    }

    [WindowsFact]
    public async Task ACpuLimitCapsTheShareOfTheMachine()
    {
        await using var process = await Start(ConsoleMode.Pipes, new ProcessLimits(10, null));
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        var limited = await BurnAsync(process, output, 1);
        process.ApplyLimits(new ProcessLimits(50, null));
        var raised = await BurnAsync(process, output, 2);
        process.ApplyLimits(new ProcessLimits(5, null));
        var lowered = await BurnAsync(process, output, 3);

        // A hard cap of a share of every processor (with room for scheduling granularity), changed at once both ways.
        Assert.True(limited <= 0.15, $"Limited to 10 %: {limited:P0} of the machine");
        Assert.True(raised > limited * 2 && raised <= 0.6, $"Limited: {limited:P0}, raised to 50 %: {raised:P0}");
        Assert.True(lowered <= 0.1, $"Lowered to 5 %: {lowered:P0}");
    }

    private static async Task<double> BurnAsync(IGameProcess process, OutputCollector output, int round)
    {
        var before = process.SampleUsage().CpuTime;
        var clock = Stopwatch.StartNew();
        await process.WriteInputAsync("burn 2\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("Test server: burned", occurrence: round);
        return (process.SampleUsage().CpuTime - before).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [WindowsFact]
    public async Task TheExitCodeOfACrashIsReported()
    {
        await using var process = await Start(ConsoleMode.Pipes, "--exit-after", "200", "42");

        Assert.Equal(42, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [WindowsFact]
    public async Task TerminatingTheTreeKillsChildrenAndGrandchildren()
    {
        await using var process = await Start(ConsoleMode.Pty);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        await process.WriteInputAsync("spawn\r\n", TestContext.Current.CancellationToken);
        var text = await output.WaitForAsync("grandchild");
        var pids = ProcessIds(text);
        Assert.Equal(3, process.CountProcesses());

        process.TerminateTree(1);

        Assert.Equal(1, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await WaitUntilGone([process.ProcessId, .. pids]);
        Assert.Equal(0, process.CountProcesses());
    }

    [WindowsFact]
    public async Task DisposingTheProcessLeavesNothingRunning()
    {
        var process = await Start(ConsoleMode.Pipes);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");
        await process.WriteInputAsync("spawn\n", TestContext.Current.CancellationToken);
        var pids = ProcessIds(await output.WaitForAsync("grandchild"));

        await process.DisposeAsync();

        await WaitUntilGone([process.ProcessId, .. pids]);
        await output.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [WindowsFact]
    public async Task OnlyTheConsoleHandlesAreInherited()
    {
        // Push the handle value far above what a fresh process uses, so it cannot match one of the child's own by chance.
        var filler = Enumerable.Range(0, 2000).Select(_ => new EventWaitHandle(false, EventResetMode.ManualReset)).ToList();
        using var inheritable = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var value = inheritable.ClientSafePipeHandle.DangerousGetHandle();
        filler.ForEach(h => h.Dispose());

        foreach (var mode in new[] { ConsoleMode.Pipes, ConsoleMode.Pty })
        {
            await using var process = await Start(mode);
            var output = new OutputCollector(process);
            await output.WaitForAsync("Test server ready");

            await process.WriteInputAsync($"handle {value}\r\n", TestContext.Current.CancellationToken);

            await output.WaitForAsync($"Test server: handle {value} invalid");
        }
    }

    [WindowsFact]
    public async Task TheServerGetsItsOwnTemporaryDirectoryAndTheManifestEnvironment()
    {
        var spec = _server.Spec(ConsoleMode.Pipes) with { Environment = new Dictionary<string, string> { ["GAME_MODE"] = "casual" } };
        await using var process = await TestServerDirectory.Manager().StartAsync(spec, TestContext.Current.CancellationToken);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        await process.WriteInputAsync("env TEMP\nenv GAME_MODE\n", TestContext.Current.CancellationToken);

        await output.WaitForAsync($"Test server: TEMP={Path.Combine(_server.Root, ".pulsar", "temp")}");
        await output.WaitForAsync("Test server: GAME_MODE=casual");
    }

    [WindowsFact]
    public async Task AGameCanTakeAFolderOfTheServerForItsUsersAppData()
    {
        // SCP: Secret Laboratory's LabAPI keeps its plugins in its account's AppData: here, in the server's folder.
        var spec = _server.Spec(ConsoleMode.Pipes) with { UserProfile = "profile" };
        await using var process = await TestServerDirectory.Manager().StartAsync(spec, TestContext.Current.CancellationToken);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        await process.WriteInputAsync("env USERPROFILE\nenv APPDATA\nenv LOCALAPPDATA\n", TestContext.Current.CancellationToken);

        var profile = Path.Combine(_server.Root, "profile");
        await output.WaitForAsync($"Test server: USERPROFILE={profile}");
        await output.WaitForAsync($"Test server: APPDATA={Path.Combine(profile, "AppData", "Roaming")}");
        await output.WaitForAsync($"Test server: LOCALAPPDATA={Path.Combine(profile, "AppData", "Local")}");
        Assert.True(Directory.Exists(Path.Combine(profile, "AppData", "Roaming")));
        Assert.True(Directory.Exists(Path.Combine(profile, "AppData", "Local")));
    }

    [WindowsFact]
    public void AnAppDataFolderFollowsTheProfileThatWindowsExpandsItFrom()
    {
        // What makes the folder above the game's: its account's User Shell Folders name AppData by %USERPROFILE%.
        var environment = WindowsGameProcessManager.BuildEnvironment(new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\pulsar-abcd2345" },
            new Dictionary<string, string>(), @"D:\servers\abcd2345\.pulsar\temp", @"D:\servers\abcd2345\profile");

        Assert.Contains("USERPROFILE=D:\\servers\\abcd2345\\profile\0", environment, StringComparison.Ordinal);
        Assert.Contains("APPDATA=D:\\servers\\abcd2345\\profile\\AppData\\Roaming\0", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("pulsar-abcd2345", environment, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ReservedEnvironmentVariablesCannotBeOverridden()
    {
        var spec = _server.Spec(ConsoleMode.Pipes) with { Environment = new Dictionary<string, string> { ["Path"] = @"C:\evil" } };

        var error = await Assert.ThrowsAsync<GameProcessException>(() => TestServerDirectory.Manager().StartAsync(spec, TestContext.Current.CancellationToken));

        Assert.Equal("process.invalid_environment", error.Code);
    }

    [WindowsFact]
    public async Task ExecutablesOutsideTheServerDirectoryOrThroughJunctionsAreRefused()
    {
        var manager = TestServerDirectory.Manager();
        var outside = _server.Spec(ConsoleMode.Pipes) with { Executable = "../escape.exe" };
        Assert.Equal("process.path_outside_server",
            (await Assert.ThrowsAsync<GameProcessException>(() => manager.StartAsync(outside, TestContext.Current.CancellationToken))).Code);

        var absolute = _server.Spec(ConsoleMode.Pipes) with { Executable = Environment.ProcessPath! };
        Assert.Equal("process.invalid_path",
            (await Assert.ThrowsAsync<GameProcessException>(() => manager.StartAsync(absolute, TestContext.Current.CancellationToken))).Code);

        var notExe = _server.Spec(ConsoleMode.Pipes) with { Executable = "bin/Pulsar.TestGameServer.dll" };
        Assert.Equal("process.invalid_executable",
            (await Assert.ThrowsAsync<GameProcessException>(() => manager.StartAsync(notExe, TestContext.Current.CancellationToken))).Code);

        // A junction inside the server directory pointing at the real binaries: the path is refused all the same.
        CreateJunction(Path.Combine(_server.Root, "link"), Path.Combine(_server.Root, "bin"));
        var throughJunction = _server.Spec(ConsoleMode.Pipes) with { Executable = "link/Pulsar.TestGameServer.exe" };
        Assert.Equal("process.reparse_point",
            (await Assert.ThrowsAsync<GameProcessException>(() => manager.StartAsync(throughJunction, TestContext.Current.CancellationToken))).Code);
    }

    [WindowsFact]
    public void AStrayProcessIsKilledOnlyWhenTheHandleItIsKilledThroughRunsAsTheAccount()
    {
        using var process = Process.Start(new ProcessStartInfo("ping.exe", ["-n", "60", "127.0.0.1"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        try
        {
            // An account it does not run as (not SYSTEM: run as SYSTEM, the CI step, ping is SYSTEM's).
            var other = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalServiceSid, null);
            Assert.False(WindowsGameProcessManager.TerminateIfRunningAs(process.Id, other));
            Assert.False(process.WaitForExit(200));

            Assert.True(WindowsGameProcessManager.TerminateIfRunningAs(process.Id, System.Security.Principal.WindowsIdentity.GetCurrent().User!));
            Assert.True(process.WaitForExit(10_000));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [WindowsFact]
    public void AnExecutableWhoseFinalPathIsLongerThan1024CharactersResolves()
    {
        // Windows paths reach 32,767 characters: an administrator's junction can lead the servers root somewhere deep.
        var deep = Directory.CreateDirectory(Path.Combine([_server.Root, .. Enumerable.Repeat(new string('d', 200), 5), "server"])).FullName;
        Directory.CreateDirectory(Path.Combine(deep, "bin"));
        File.WriteAllText(Path.Combine(deep, "bin", "game.exe"), "");
        Assert.True(deep.Length > 1024);
        var alias = Path.Combine(_server.Root, "alias");
        CreateJunction(alias, deep);

        Assert.Equal(Path.Combine(alias, "bin", "game.exe"), ServerPaths.ResolveFile(alias, "bin/game.exe"));
    }

    [WindowsFact]
    public async Task AJunctionInPlaceOfThePulsarFolderIsNotFollowedForTheTemporaryDirectory()
    {
        // The server's account controls its directory: its .pulsar folder could be a junction to anywhere.
        var outside = Directory.CreateTempSubdirectory("pulsar-outside-");
        try
        {
            CreateJunction(Path.Combine(_server.Root, ".pulsar"), outside.FullName);

            var refused = await Assert.ThrowsAsync<GameProcessException>(() =>
                TestServerDirectory.Manager().StartAsync(_server.Spec(ConsoleMode.Pipes), TestContext.Current.CancellationToken));

            Assert.Equal("files.reparse_point", refused.Code);
            Assert.Empty(outside.EnumerateFileSystemInfos());
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [WindowsFact]
    public async Task AWriteAGameDoesNotTakeCanBeGivenUpAndTheProcessStillEnds()
    {
        // A frozen game: it never reads its input, so the pipe fills and the next write waits for it.
        var process = await TestServerDirectory.Manager().StartAsync(_server.Spec(ConsoleMode.Pipes, "--sleep"), TestContext.Current.CancellationToken);
        var line = new string('x', 64 * 1024) + "\n";

        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(async () =>
            {
                while (true)
                {
                    await process.WriteInputAsync(line, wait.Token);
                }
            }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        // The next one waits behind it, and can give up too.
        using (var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WriteInputAsync("say hi\n", wait.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => process.WriteInputAsync("say hi\n", TestContext.Current.CancellationToken).AsTask());
    }

    private Task<IGameProcess> Start(ConsoleMode mode, params string[] arguments) =>
        TestServerDirectory.Manager().StartAsync(_server.Spec(mode, ["--ready-after", "100", .. arguments]), TestContext.Current.CancellationToken);

    private Task<IGameProcess> Start(ConsoleMode mode, ProcessLimits limits) =>
        TestServerDirectory.Manager().StartAsync(_server.Spec(mode, ["--ready-after", "100"]) with { Limits = limits }, TestContext.Current.CancellationToken);

    private static int[] ProcessIds(string output) =>
        [.. System.Text.RegularExpressions.Regex.Matches(output, @"(?:child|grandchild) (\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];

    private static async Task WaitUntilGone(IEnumerable<int> processIds)
    {
        foreach (var pid in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(await Task.Run(() => process.WaitForExit(10_000)), $"Process {pid} is still running.");
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // Junctions need no privilege (symbolic links do).
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }
}
