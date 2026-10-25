using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// The installers the agent runs (Forge's and NeoForge's, Pulsar 1.0.9): the server's Java runtime, as the server's
/// account, in its folder, what they print in its console; one that fails or stalls is reported and stopped.
/// </summary>
public sealed class InstallJavaRunnerTests
{
    private static readonly Guid s_server = Guid.NewGuid();
    private readonly FakeProcessManager _processes = new();
    private readonly List<string> _console = [];

    [Fact]
    public async Task TheRuntimeRunsAsTheServerInItsFolderItsOutputInTheConsole()
    {
        _processes.OnStarted = (process, _) =>
        {
            process.Print("Downloading library net.minecraftforge:forge\n");
            process.Exit(0);
        };

        await Runner()!(["-jar", ".pulsar/minecraft-installer.jar", "--installServer"], TestContext.Current.CancellationToken);

        var spec = Assert.Single(_processes.Specs);
        Assert.Equal(("java/bin/java.exe", "", @"D:\servers\abcd1234", ConsoleMode.Pipes, ProcessIdentity.CurrentUser, s_server),
            (spec.Executable, spec.WorkingDirectory, spec.ServerRoot, spec.ConsoleMode, spec.Identity, spec.ServerId));
        Assert.Equal(["-jar", ".pulsar/minecraft-installer.jar", "--installServer"], spec.Arguments);
        Assert.Equal(["Downloading library net.minecraftforge:forge\n"], _console);
    }

    [Fact]
    public async Task AnInstallerThatFailsOrStallsIsReportedAndStopped()
    {
        _processes.OnStarted = (process, _) => process.Exit(1);
        var failed = await Assert.ThrowsAsync<GameProcessException>(() => Runner()!(["-jar", "x.jar"], TestContext.Current.CancellationToken));
        Assert.Equal("minecraft.installer_failed", failed.Code);

        _processes.OnStarted = (process, _) => process.Print("Starting\n"); // then nothing more
        var stalled = await Assert.ThrowsAsync<GameProcessException>(() => Runner()!(["-jar", "x.jar"], TestContext.Current.CancellationToken));
        Assert.Equal("minecraft.installer_stalled", stalled.Code);
        Assert.Equal(1, _processes.Last.TerminatedWith);
    }

    [Fact]
    public void NothingRunsWithoutTheServerOrAWayToStartProcesses()
    {
        Assert.Null(new InstallSteps(new InstallOptions(), new DirectServerFiles()).JavaFor(Context(), @"D:\servers\abcd1234"));
        Assert.Null(new InstallSteps(new InstallOptions(), new DirectServerFiles(), _processes).JavaFor(null, @"D:\servers\abcd1234"));
    }

    private JavaRunner? Runner() =>
        new InstallSteps(new InstallOptions { InstallerStallTimeout = TimeSpan.FromMilliseconds(300) }, new DirectServerFiles(), _processes)
            .JavaFor(Context(), @"D:\servers\abcd1234");

    private InstallContext Context() => new(s_server, ProcessIdentity.CurrentUser, _console.Add);
}
