using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Needs an installed Counter-Strike: Source dedicated server (<c>PULSAR_SRCDS_DIR</c>) and the account test conditions
/// (SYSTEM on a disposable machine): scripts/sandbox/real-game runs it in Windows Sandbox.
/// </summary>
public sealed class SrcdsFactAttribute : FactAttribute
{
    public SrcdsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Needs PULSAR_SRCDS_DIR (a CS:S dedicated server) and SYSTEM on a disposable machine (scripts/sandbox/real-game).";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(SrcdsFactAttribute);
    }

    public static bool Enabled => AccountFactAttribute.Enabled && AccountFactAttribute.IsSystem
        && Environment.GetEnvironmentVariable("PULSAR_SRCDS_DIR") is { Length: > 0 } directory && File.Exists(Path.Combine(directory, "srcds.exe"));
}

/// <summary>
/// A real game server started the way the agent service starts it (docs/architecture/06): its own account and restricted
/// token, its own window station, inside a Job with UI restrictions, from SYSTEM in session 0. srcds creates a console
/// window of its own (spike 0001), which is what could fail to initialize there (spike 0002, "not answered").
/// </summary>
public sealed class RealGameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-real-game", Guid.NewGuid().ToString("N"));
    private readonly string _shortId = "g" + Guid.NewGuid().ToString("N")[..9];

    public void Dispose()
    {
        if (SrcdsFactAttribute.Enabled)
        {
            Provisioner().Deprovision(_root, _shortId);
            WindowsServerProvisioner.DeleteTree(_root);
        }
    }

    [SrcdsFact]
    public async Task CounterStrikeSourceRunsAsItsOwnAccountFromSystem()
    {
        var server = Provisioner().Provision(_root, _shortId);
        Copy(Environment.GetEnvironmentVariable("PULSAR_SRCDS_DIR")!, server.Directory);
        // An installation that already ran has an old log (-condebug appends): this run must write its own.
        var log = Path.Combine(server.Directory, "cstrike", "console.log");
        File.Delete(log);
        var spec = new ProcessLaunchSpec(Guid.NewGuid(), server.Directory, "srcds.exe",
            ["-game", "cstrike", "-console", "-condebug", "-port", "27215", "+map", "de_dust2", "+maxplayers", "8", "-nohltv"], "",
            new Dictionary<string, string>(), ConsoleMode.Pty, ConsoleEncoding.Utf8, server.Identity);

        await using var process = await TestServerDirectory.Manager().StartAsync(spec, TestContext.Current.CancellationToken);
        var output = new OutputCollector(process);
        var started = Stopwatch.StartNew();
        var ready = false;
        while (!ready && started.Elapsed < TimeSpan.FromMinutes(3) && !process.Exited.IsCompleted)
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            ready = File.Exists(log) && Read(log) is var text && text.Contains("ports 27215", StringComparison.Ordinal)
                && text.Contains("hibernating", StringComparison.OrdinalIgnoreCase);
        }

        var exit = process.Exited.IsCompleted ? $"exited with 0x{await process.Exited:X8}" : "running";
        TestContext.Current.SendDiagnosticMessage($"srcds {exit} after {started.Elapsed.TotalSeconds:0.0} s; processes in its job: {process.CountProcesses()}");
        TestContext.Current.SendDiagnosticMessage($"pseudo console output: {output.Text.Length} characters");
        TestContext.Current.SendDiagnosticMessage("console.log:\n" + (File.Exists(log) ? Tail(Read(log), 40) : "(missing)"));
        Assert.True(ready, $"srcds did not reach 'hibernating' ({exit}).");

        // Still alive a while later: no delayed failure once it has its window station and console.
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(process.Exited.IsCompleted, "srcds exited after reporting it was ready.");

        process.TerminateTree(1);
        Assert.Equal(1, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(0, process.CountProcesses());
    }

    private static WindowsServerProvisioner Provisioner() => new(new WindowsServerAccounts(NullLogger<WindowsServerAccounts>.Instance),
        development: false, NullLogger<WindowsServerProvisioner>.Instance);

    // srcds keeps console.log open for writing.
    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Tail(string text, int lines) => string.Join('\n', text.Split('\n').TakeLast(lines));

    private static void Copy(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
