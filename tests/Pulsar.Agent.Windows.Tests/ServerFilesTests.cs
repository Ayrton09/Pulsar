using System.Diagnostics;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// The agent reads and writes files in directories the server's account controls (configuration files, the game's
/// log, its SteamCMD): junctions and links planted there must never take it outside.
/// </summary>
public sealed class ServerFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-files-tests", Guid.NewGuid().ToString("N"));
    private readonly string _server;
    private readonly string _outside;
    private readonly WindowsServerFiles _files = new();

    public ServerFilesTests()
    {
        _server = Directory.CreateDirectory(Path.Combine(_root, "server")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "secret.cfg"), "rcon_password other-server");
    }

    public void Dispose() => WindowsServerProvisioner.DeleteTree(_root);

    [WindowsFact]
    public void WritesReadsAndDeletesInsideTheServer()
    {
        _files.Write(_server, "game/csgo/cfg/server.cfg", Content("hostname \"x\""));
        using (var stream = _files.OpenRead(_server, "game/csgo/cfg/server.cfg")!)
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal("hostname \"x\"", reader.ReadToEnd());
        }

        _files.Delete(_server, "game/csgo/cfg/server.cfg");
        Assert.Null(_files.OpenRead(_server, "game/csgo/cfg/server.cfg"));
        _files.Delete(_server, "game/csgo/cfg/server.cfg"); // already gone: nothing to do
    }

    [WindowsFact]
    public void AJunctionNeverTakesTheAgentOutside()
    {
        Run("mklink", "/J", Path.Combine(_server, "game"), _outside);

        Assert.Equal("files.reparse_point", Assert.Throws<GameProcessException>(() => _files.Write(_server, "game/new.cfg", Content("x"))).Code);
        Assert.Throws<GameProcessException>(() => _files.OpenRead(_server, "game/secret.cfg"));
        Assert.Throws<GameProcessException>(() => _files.Delete(_server, "game/secret.cfg"));
        Assert.Equal(["secret.cfg"], Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName));
        Assert.Equal("rcon_password other-server", File.ReadAllText(Path.Combine(_outside, "secret.cfg")));
    }

    [WindowsFact]
    public void AJunctionDeeperInThePathIsCaughtToo()
    {
        Directory.CreateDirectory(Path.Combine(_server, "game", "csgo"));
        Run("mklink", "/J", Path.Combine(_server, "game", "csgo", "cfg"), _outside);

        Assert.Throws<GameProcessException>(() => _files.OpenRead(_server, "game/csgo/cfg/secret.cfg"));
        Assert.Throws<GameProcessException>(() => _files.Write(_server, "game/csgo/cfg/server.cfg", Content("x")));
        Assert.False(File.Exists(Path.Combine(_outside, "server.cfg")));
    }

    [WindowsFact]
    public void AFolderSwappedForAJunctionWhileWritingLeavesNothingOutside()
    {
        // The server's account swaps a folder the agent has just checked for a junction to somewhere else.
        Directory.CreateDirectory(Path.Combine(_server, "game", "cfg"));
        var files = new WindowsServerFiles
        {
            BeforeCreate = () =>
            {
                Directory.Move(Path.Combine(_server, "game", "cfg"), Path.Combine(_server, "game", "old"));
                Run("mklink", "/J", Path.Combine(_server, "game", "cfg"), _outside);
            },
        };

        try
        {
            files.Write(_server, "game/cfg/new.cfg", Content("x"));
        }
        catch (GameProcessException)
        {
            // Refusing is fine too; leaving a file outside is not.
        }

        Assert.Equal(["secret.cfg"], Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName));
    }

    [WindowsFact]
    public void AServerDirectoryReachedThroughAJunctionWorks()
    {
        // An administrator's junction (the servers root on another drive, say) is followed; only what is below it counts.
        var alias = Path.Combine(_root, "alias");
        Run("mklink", "/J", alias, _server);

        _files.Write(alias, "game/server.cfg", Content("x"));

        using var stream = _files.OpenRead(alias, "game/server.cfg")!;
        Assert.Equal("x", new StreamReader(stream).ReadToEnd());
        Assert.True(File.Exists(Path.Combine(_server, "game", "server.cfg")));
    }

    [WindowsFact]
    public void AHardLinkIsReplacedNotWrittenInto()
    {
        Run("mklink", "/H", Path.Combine(_server, "server.cfg"), Path.Combine(_outside, "secret.cfg"));

        _files.Write(_server, "server.cfg", Content("mine"));

        Assert.Equal("rcon_password other-server", File.ReadAllText(Path.Combine(_outside, "secret.cfg")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_server, "server.cfg")));
    }

    [WindowsFact]
    public void DeletingAJunctionRemovesTheLinkNotItsTarget()
    {
        Run("mklink", "/J", Path.Combine(_server, "logs"), _outside);

        _files.Delete(_server, "logs");

        Assert.False(Directory.Exists(Path.Combine(_server, "logs")));
        Assert.True(File.Exists(Path.Combine(_outside, "secret.cfg")));
    }

    [WindowsFact]
    public void OnlyAnEmptyRealDirectoryIsRemoved()
    {
        Directory.CreateDirectory(Path.Combine(_server, "addons", "empty"));
        File.WriteAllText(Path.Combine(_server, "addons", "kept.cfg"), "x");
        Run("mklink", "/J", Path.Combine(_server, "link"), Directory.CreateDirectory(Path.Combine(_outside, "empty")).FullName);

        Assert.True(_files.DeleteEmptyDirectory(_server, "addons/empty"));
        Assert.False(_files.DeleteEmptyDirectory(_server, "addons")); // holds kept.cfg
        Assert.False(_files.DeleteEmptyDirectory(_server, "link")); // a junction, even to an empty folder
        Assert.False(_files.DeleteEmptyDirectory(_server, "missing"));
        Assert.False(_files.DeleteEmptyDirectory(_server, "addons/kept.cfg")); // a file

        Assert.False(Directory.Exists(Path.Combine(_server, "addons", "empty")));
        Assert.True(File.Exists(Path.Combine(_server, "addons", "kept.cfg")));
        Assert.True(Directory.Exists(Path.Combine(_server, "link")));
        Assert.True(Directory.Exists(Path.Combine(_outside, "empty")));
    }

    [WindowsFact]
    public void ReadingSharesTheFileWithTheGameWritingIt()
    {
        using var game = new FileStream(Path.Combine(_server, "console.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        game.Write("line 1\n"u8);
        game.Flush();

        using var stream = _files.OpenRead(_server, "console.log")!;
        game.Write("line 2\n"u8);
        game.Flush();

        Assert.Equal("line 1\nline 2\n", new StreamReader(stream).ReadToEnd());
    }

    [WindowsFact]
    public void PathsMustBeRelativeAndValid()
    {
        Assert.Equal("files.invalid_path", Assert.Throws<GameProcessException>(() => _files.OpenRead(_server, "../outside/secret.cfg")).Code);
        Assert.Equal("files.invalid_path", Assert.Throws<GameProcessException>(() => _files.Write(_server, "C:/Windows/x.cfg", Content("x"))).Code);
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    private static void Run(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", .. arguments])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
