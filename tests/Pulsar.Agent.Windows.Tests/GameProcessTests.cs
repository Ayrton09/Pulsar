using System.Diagnostics;
using System.IO.Pipes;
using Pulsar.Agent.Core.Processes;

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
        Assert.False(process.SupportsInterrupt);
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

    private Task<IGameProcess> Start(ConsoleMode mode, params string[] arguments) =>
        TestServerDirectory.Manager().StartAsync(_server.Spec(mode, ["--ready-after", "100", .. arguments]), TestContext.Current.CancellationToken);

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
