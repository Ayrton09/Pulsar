using System.Diagnostics;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Install steps run with the agent's rights (SYSTEM) in a directory the server's account controls: a server must not
/// be able to redirect those writes outside its directory with a junction or a hard link.
/// </summary>
public sealed class InstallStepsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-install-tests", Guid.NewGuid().ToString("N"));
    private readonly string _server;
    private readonly string _outside;
    private readonly string _testGame;

    public InstallStepsTests()
    {
        _server = Directory.CreateDirectory(Path.Combine(_root, "server")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        _testGame = Directory.CreateDirectory(Path.Combine(_root, "testgame")).FullName;
        File.WriteAllText(Path.Combine(_testGame, "Pulsar.TestGameServer.exe"), "new build");
    }

    // The junction test leaves one behind, which only the link-safe delete removes without elevation.
    public void Dispose() => WindowsServerProvisioner.DeleteTree(_root);

    [WindowsFact]
    public void TheStepsInstallTheTestGameAndCreateDirectories()
    {
        Steps().Run(Manifest(), _server);

        Assert.Equal("new build", File.ReadAllText(Path.Combine(_server, "bin", "Pulsar.TestGameServer.exe")));
        Assert.True(Directory.Exists(Path.Combine(_server, "data")));
    }

    [WindowsFact]
    public void TheAgentDoesNotWriteThroughAJunctionThePlayerPlanted()
    {
        Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(_server, "bin"), _outside);

        var error = Assert.Throws<GameProcessException>(() => Steps().Run(Manifest(), _server));

        Assert.Equal("install.reparse_point", error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [WindowsFact]
    public void AHardLinkToAnotherFileIsReplacedNotWrittenInto()
    {
        var precious = Path.Combine(_outside, "precious.dll");
        File.WriteAllText(precious, "keep me");
        Directory.CreateDirectory(Path.Combine(_server, "bin"));
        Run("cmd.exe", "/c", "mklink", "/H", Path.Combine(_server, "bin", "Pulsar.TestGameServer.exe"), precious);

        Steps().Run(Manifest(), _server);

        Assert.Equal("keep me", File.ReadAllText(precious));
        Assert.Equal("new build", File.ReadAllText(Path.Combine(_server, "bin", "Pulsar.TestGameServer.exe")));
    }

    [WindowsFact]
    public void WithoutTheTestGameTheStepIsRefused()
    {
        var error = Assert.Throws<GameProcessException>(() => new InstallSteps(new InstallOptions()).Run(Manifest(), _server));

        Assert.Equal("install.test_game_unavailable", error.Code);
    }

    private InstallSteps Steps() => new(new InstallOptions { TestGameDirectory = _testGame });

    private static GameManifest Manifest()
    {
        var path = Path.Combine(RepositoryRoot(), "games", "_test", "manifest.json");
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(File.ReadAllText(path)));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        return result.Manifest!;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Pulsar.slnx not found.");
    }

    private static void Run(string file, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
