using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Tests;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// The Windows file manager against real junctions, symbolic links and hard links (docs/architecture/08, "File
/// manager"): whatever a server plants in its directory, the file manager never reads, writes, renames or deletes
/// anything outside it, and never follows a link.
/// </summary>
public sealed class WindowsServerFileManagerTests : ServerFileManagerContract
{
    private readonly string _outside;

    public WindowsServerFileManagerTests()
    {
        _outside = Directory.CreateDirectory(Path.Combine(Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "secret.cfg"), "rcon_password other-server");
        Directory.CreateDirectory(Path.Combine(_outside, "sub"));
        Files = new WindowsServerFileManager(new ServerImpersonation(TimeProvider.System));
    }

    protected override IServerFileManager Files { get; }

    // .NET's recursive delete removes junctions through DeleteVolumeMountPoint, which needs elevation.
    protected override void DeleteRoot() => WindowsServerProvisioner.DeleteTree(Root);

    [WindowsFact]
    public void AJunctionToTheOutsideIsListedButNeverFollowed()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "mine");
        Link("/J", "game", _outside);

        var entry = Files.List(Server, ServerFilePath.Root).Entries.Single(e => e.Name == "game");
        Assert.Equal(FileEntryKind.Link, entry.Kind);
        Assert.Equal(FileErrors.Link, Code(() => Files.List(Server, "game")));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.OpenRead(Server, "game/secret.cfg", 100)));
        Assert.Equal(FileErrors.Link, Code(() => Files.Create(Server, "game/new.cfg", 1, overwrite: true, expectedVersion: null)));
        Assert.Equal(FileErrors.Link, Code(() => Files.CreateDirectory(Server, "game/sub")));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.Move(Server, "game/secret.cfg", "stolen.cfg")));
        Assert.Equal(FileErrors.Link, Code(() => Files.Move(Server, "server.cfg", "game/server.cfg")));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.Delete(Server, "game/secret.cfg")));
        Assert.Equal(FileErrors.Link, Code(() => Files.OpenRead(Server, "game", 100)));
        OutsideIsUntouched();
    }

    [WindowsFact]
    public void AJunctionDeeperInThePathIsCaughtToo()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "game", "csgo"));
        Link("/J", "game/csgo/cfg", _outside);

        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.OpenRead(Server, "game/csgo/cfg/secret.cfg", 100)));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.Create(Server, "game/csgo/cfg/sub/server.cfg", 1, overwrite: true, expectedVersion: null)));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.List(Server, "game/csgo/cfg/sub")));
        Assert.Equal(FileErrors.Link, Code(() => Files.CreateDirectory(Server, "game/csgo/cfg/sub/x")));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.Move(Server, "game/csgo/cfg/sub", "sub")));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.Delete(Server, "game/csgo/cfg/secret.cfg")));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.Delete(Server, "game/csgo/cfg/sub")));
        OutsideIsUntouched();
    }

    [WindowsFact]
    public void ALinkInsideTheServerIsNotFollowedEither()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "real"));
        File.WriteAllText(Path.Combine(ServerDirectory, "real", "a.cfg"), "a");
        Link("/J", "alias", Path.Combine(ServerDirectory, "real"));

        Assert.Equal(FileErrors.Link, Code(() => Files.List(Server, "alias")));
        Files.Move(Server, "alias", "renamed");
        Files.Delete(Server, "renamed");

        Assert.Equal("a", File.ReadAllText(Path.Combine(ServerDirectory, "real", "a.cfg")));
    }

    [WindowsFact]
    public void SymbolicLinksAreLinksToo()
    {
        SkipUnlessSymbolicLinksAllowed();
        Link("", "secret.cfg", Path.Combine(_outside, "secret.cfg"));
        Link("/D", "dir", _outside);

        Assert.All(Files.List(Server, ServerFilePath.Root).Entries, e => Assert.Equal(FileEntryKind.Link, e.Kind));
        Assert.Equal(FileErrors.Link, Code(() => Files.OpenRead(Server, "secret.cfg", 100)));
        Assert.Equal(FileErrors.Link, Code(() => Files.Create(Server, "secret.cfg", 1, overwrite: true, expectedVersion: null)));
        Assert.Equal(FileErrors.Link, Code(() => Files.List(Server, "dir")));
        Assert.Equal(FileErrors.OutsideServer, Code(() => Files.OpenRead(Server, "dir/secret.cfg", 100)));

        Files.Delete(Server, "secret.cfg");
        Files.Delete(Server, "dir");

        Assert.Empty(Directory.EnumerateFileSystemEntries(ServerDirectory));
        OutsideIsUntouched();
    }

    [WindowsFact]
    public void AHardLinkIsReplacedOrRemovedNeverWrittenInto()
    {
        Link("/H", "server.cfg", Path.Combine(_outside, "secret.cfg"));
        Link("/H", "other.cfg", Path.Combine(_outside, "secret.cfg"));

        using (var pending = Files.Create(Server, "server.cfg", 4, overwrite: true, expectedVersion: null))
        {
            pending.Content.Write("mine"u8);
            pending.Commit();
        }

        Files.Delete(Server, "other.cfg");

        Assert.Equal("mine", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));
        Assert.False(File.Exists(Path.Combine(ServerDirectory, "other.cfg")));
        OutsideIsUntouched();
    }

    [WindowsFact]
    public void DeletingAFolderNeverEmptiesWhatALinkInItPointsAt()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "addons", "deep"));
        File.WriteAllText(Path.Combine(ServerDirectory, "addons", "deep", "a.smx"), "a");
        Link("/J", "addons/deep/escape", _outside);
        Link("/H", "addons/hard.cfg", Path.Combine(_outside, "secret.cfg"));

        Files.Delete(Server, "addons");

        Assert.Empty(Directory.EnumerateFileSystemEntries(ServerDirectory));
        OutsideIsUntouched();
    }

    [WindowsFact]
    public void PathsLongerThanTheOldWindowsLimitWork()
    {
        var path = string.Join('/', Enumerable.Repeat("a-rather-long-folder-name-for-a-game-server", 8));
        Assert.True(Path.Join(ServerDirectory, path).Length > 260);

        Files.CreateDirectory(Server, path);
        using (var pending = Files.Create(Server, path + "/server.cfg", 1, overwrite: false, expectedVersion: null))
        {
            pending.Content.Write("x"u8);
            pending.Commit();
        }

        Assert.Equal("x", Text(Files.OpenRead(Server, path + "/server.cfg", 100)));
        Assert.Single(Files.List(Server, path).Entries);
        Files.Delete(Server, path.Split('/')[0]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ServerDirectory));
    }

    [WindowsFact]
    public void AServerDirectoryReachedThroughAnAdministratorsJunctionWorks()
    {
        var alias = Path.Combine(Root, "alias");
        Run("mklink", "/J", alias, ServerDirectory);
        var server = new ProvisionedServer(alias, ProcessIdentity.CurrentUser);

        Files.CreateDirectory(server, "cfg");
        using (var pending = Files.Create(server, "cfg/server.cfg", 1, overwrite: false, expectedVersion: null))
        {
            pending.Content.Write("x"u8);
            pending.Commit();
        }

        Assert.Equal("x", File.ReadAllText(Path.Combine(ServerDirectory, "cfg", "server.cfg")));
        Assert.Equal(["cfg"], Files.List(server, ServerFilePath.Root).Entries.Select(e => e.Name));
    }

    [WindowsFact]
    public void AFileTheGameHoldsOpenCanStillBeRead()
    {
        using var game = new FileStream(Path.Combine(ServerDirectory, "console.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        game.Write("line 1\n"u8);
        game.Flush();

        Assert.Equal("line 1\n", Text(Files.OpenRead(Server, "console.log", 100)));
    }

    /// <summary>
    /// Layer 4: acting as the server's account, the file manager cannot read another server's file even through a hard
    /// link planted in its own directory (the agent itself, as SYSTEM, could).
    /// </summary>
    [AccountFact]
    public void AsTheServersAccountAnotherServersFileStaysUnreadable()
    {
        var provisioner = new WindowsServerProvisioner(new WindowsServerAccounts(NullLogger<WindowsServerAccounts>.Instance),
            development: false, NullLogger<WindowsServerProvisioner>.Instance);
        var serversRoot = Path.Combine(Root, "servers");
        string[] shortIds = ["t" + Guid.NewGuid().ToString("N")[..9], "t" + Guid.NewGuid().ToString("N")[..9]];
        try
        {
            var a = provisioner.Provision(serversRoot, shortIds[0]);
            var b = provisioner.Provision(serversRoot, shortIds[1]);
            File.WriteAllText(Path.Combine(b.Directory, "secret.cfg"), "server b");
            Run("mklink", "/H", Path.Combine(a.Directory, "stolen.cfg"), Path.Combine(b.Directory, "secret.cfg"));
            using var impersonation = new ServerImpersonation(TimeProvider.System);
            var files = new WindowsServerFileManager(impersonation);

            Assert.Equal(FileErrors.AccessDenied, Code(() => files.OpenRead(a, "stolen.cfg", 100)));
            Assert.Equal(FileErrors.AccessDenied, Code(() => files.List(b with { Identity = a.Identity }, ServerFilePath.Root)));

            files.CreateDirectory(a, "cfg");
            using (var pending = files.Create(a, "cfg/server.cfg", 4, overwrite: false, expectedVersion: null))
            {
                pending.Content.Write("mine"u8);
                pending.Commit();
            }

            Assert.Equal("mine", Text(files.OpenRead(a, "cfg/server.cfg", 100)));
            Assert.Contains(files.List(a, ServerFilePath.Root).Entries, e => e.Name == "stolen.cfg");
            files.Delete(a, "stolen.cfg");
            Assert.Equal("server b", File.ReadAllText(Path.Combine(b.Directory, "secret.cfg")));
        }
        finally
        {
            foreach (var shortId in shortIds)
            {
                provisioner.Deprovision(serversRoot, shortId);
            }
        }
    }

    private void OutsideIsUntouched()
    {
        Assert.Equal(["secret.cfg", "sub"], Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_outside, "sub")));
        Assert.Equal("rcon_password other-server", File.ReadAllText(Path.Combine(_outside, "secret.cfg")));
    }

    private void Link(string kind, string path, string target) =>
        Run(["mklink", .. kind.Length == 0 ? Array.Empty<string>() : [kind], Path.Combine(ServerDirectory, path.Replace('/', '\\')), target]);

    private void SkipUnlessSymbolicLinksAllowed()
    {
        var probe = Path.Combine(Root, "probe");
        try
        {
            File.CreateSymbolicLink(probe, Path.Combine(_outside, "secret.cfg"));
            File.Delete(probe);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070522))
        {
            Assert.Skip("Creating symbolic links needs administrators or Developer Mode.");
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links needs administrators or Developer Mode.");
        }
    }

    private static void Run(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", .. arguments])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
