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
        Run(Steps(), _server);

        Assert.Equal("new build", File.ReadAllText(Path.Combine(_server, "bin", "Pulsar.TestGameServer.exe")));
        Assert.True(Directory.Exists(Path.Combine(_server, "data")));
    }

    [WindowsFact]
    public void TheAgentDoesNotWriteThroughAJunctionThePlayerPlanted()
    {
        Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(_server, "bin"), _outside);

        var error = Assert.Throws<GameProcessException>(() => Run(Steps(), _server));

        Assert.Equal("files.reparse_point", error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [WindowsFact]
    public void ADanglingJunctionIsCaughtByThePortablePathCheck()
    {
        // A junction whose target does not exist (yet): a later write would create its target's folders through it.
        // Directory.Exists looks at the junction itself, not its target, so the check still sees it.
        var target = Path.Combine(_outside, "later");
        Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(_server, "bin"), target);

        var error = Assert.Throws<GameProcessException>(() => InstallSteps.Resolve(_server, "bin/Pulsar.TestGameServer.exe"));

        Assert.Equal("install.reparse_point", error.Code);
        Assert.Equal(Path.Combine(_server, "missing", "file.txt"), InstallSteps.Resolve(_server, "missing/file.txt"));
    }

    [WindowsFact]
    public void AHardLinkToAnotherFileIsReplacedNotWrittenInto()
    {
        var precious = Path.Combine(_outside, "precious.dll");
        File.WriteAllText(precious, "keep me");
        Directory.CreateDirectory(Path.Combine(_server, "bin"));
        Run("cmd.exe", "/c", "mklink", "/H", Path.Combine(_server, "bin", "Pulsar.TestGameServer.exe"), precious);

        Run(Steps(), _server);

        Assert.Equal("keep me", File.ReadAllText(precious));
        Assert.Equal("new build", File.ReadAllText(Path.Combine(_server, "bin", "Pulsar.TestGameServer.exe")));
    }

    [WindowsFact]
    public void AShippedSettingsFileIsCopiedOnceAndNeverThroughALink()
    {
        var steps = Steps();
        var copy = new CopyFileIfMissingStep("serverconfig.xml", "pulsar-serverconfig.xml");
        File.WriteAllText(Path.Combine(_server, "serverconfig.xml"), "<ServerSettings/>");

        steps.Run(copy, _server);
        File.WriteAllText(Path.Combine(_server, "pulsar-serverconfig.xml"), "edited by the owner");
        File.WriteAllText(Path.Combine(_server, "serverconfig.xml"), "<ServerSettings>updated by the game</ServerSettings>");
        steps.Run(copy, _server);

        Assert.Equal("edited by the owner", File.ReadAllText(Path.Combine(_server, "pulsar-serverconfig.xml")));

        // A server that links the copy's source to a file outside cannot have the agent read it into its directory.
        File.Delete(Path.Combine(_server, "pulsar-serverconfig.xml"));
        File.Delete(Path.Combine(_server, "serverconfig.xml"));
        Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(_server, "config"), _outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "not yours");
        Assert.Equal("files.outside_server",
            Assert.Throws<GameProcessException>(() => steps.Run(new CopyFileIfMissingStep("config/secret.txt", "copy.txt"), _server)).Code);
        Assert.False(File.Exists(Path.Combine(_server, "copy.txt")));
        Assert.Equal("install.copy_source_missing",
            Assert.Throws<GameProcessException>(() => steps.Run(copy, _server)).Code);
    }

    [WindowsFact]
    public void WithoutTheTestGameTheStepIsRefused()
    {
        var error = Assert.Throws<GameProcessException>(() => Run(new InstallSteps(new InstallOptions(), new WindowsServerFiles()), _server));

        Assert.Equal("install.test_game_unavailable", error.Code);
    }

    private InstallSteps Steps() => new(new InstallOptions { TestGameDirectory = _testGame }, new WindowsServerFiles());

    private static void Run(InstallSteps steps, string server)
    {
        foreach (var step in Manifest().Install!.Steps)
        {
            steps.Run(step, server);
        }
    }

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
