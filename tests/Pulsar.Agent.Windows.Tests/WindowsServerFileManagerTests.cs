using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Backups;
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
    public void AWalkReportsJunctionsButNeverEntersThem()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "game", "csgo"));
        File.WriteAllText(Path.Combine(ServerDirectory, "game", "csgo", "server.cfg"), "mine");
        Link("/J", "game/csgo/cfg", _outside);
        Link("/J", "everything", Root);

        var entries = Files.Walk(Server);

        Assert.Equal(["everything", "game", "game/csgo", "game/csgo/cfg", "game/csgo/server.cfg"], entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(FileEntryKind.Link, entries.Single(e => e.Path == "game/csgo/cfg").Kind);
        Assert.Equal(FileEntryKind.Link, entries.Single(e => e.Path == "everything").Kind);
        Assert.Equal(FileErrors.Link, Code(() => Files.EnsureDirectory(Server, "game/csgo/cfg")));
        Assert.Equal(FileErrors.Link, Code(() => Files.EnsureDirectory(Server, "game/csgo/cfg/sub/x")));
        Assert.False(Directory.Exists(Path.Combine(_outside, "sub", "x")));
        OutsideIsUntouched();
    }

    [WindowsFact]
    public async Task ABackupNeverFollowsAJunction()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        File.WriteAllText(Path.Combine(ServerDirectory, "cfg", "server.cfg"), "mine");
        Link("/J", "cfg/stolen", _outside);
        Link("/J", "everything", Root);
        var store = new BackupStore(Files, NullLogger<BackupStore>.Instance);
        var location = new BackupLocation(Path.Combine(Root, "backups"), "abcd1234", Guid.NewGuid());

        var result = await store.CreateAsync(location, Server, rules: null, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        using (var archive = System.IO.Compression.ZipFile.OpenRead(location.Archive))
        {
            Assert.Equal(["cfg/server.cfg"], archive.Entries.Select(e => e.FullName));
        }

        Assert.Equal(1, result.FileCount);
        OutsideIsUntouched();
    }

    [WindowsFact]
    public async Task ARestoreNeverWritesThroughAJunction()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg", "sub"));
        File.WriteAllText(Path.Combine(ServerDirectory, "cfg", "secret.cfg"), "from the backup");
        File.WriteAllText(Path.Combine(ServerDirectory, "cfg", "sub", "x.cfg"), "from the backup");
        var store = new BackupStore(Files, NullLogger<BackupStore>.Instance);
        var location = new BackupLocation(Path.Combine(Root, "backups"), "abcd1234", Guid.NewGuid());
        var backup = await store.CreateAsync(location, Server, rules: null, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        Files.Delete(Server, "cfg");
        Link("/J", "cfg", _outside);

        var error = await Assert.ThrowsAsync<FileManagerException>(() =>
            store.RestoreAsync(location, Server, backup.Sha256, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken));

        Assert.Equal(FileErrors.Link, error.Code);
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
    public void AFileAnotherProgramHasOpenIsStillReplaced()
    {
        // What a virus scanner or the search indexer does to a file just written: read it, sharing everything.
        var target = Path.Combine(ServerDirectory, "server.cfg");
        File.WriteAllText(target, "old");
        using var scanner = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        using (var pending = Files.Create(Server, "server.cfg", 3, overwrite: true, expectedVersion: null))
        {
            pending.Content.Write("new"u8);
            pending.Commit();
        }

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("old", new StreamReader(scanner).ReadToEnd());
    }

    [WindowsFact]
    public void AReadOnlyFileIsReplacedAsItIsDeleted()
    {
        var target = Path.Combine(ServerDirectory, "server.cfg");
        File.WriteAllText(target, "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        using (var pending = Files.Create(Server, "server.cfg", 3, overwrite: true, expectedVersion: null))
        {
            pending.Content.Write("new"u8);
            pending.Commit();
        }

        Assert.Equal("new", File.ReadAllText(target));
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
    public async Task AsTheServersAccountAnotherServersFileStaysUnreadable()
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
            files.Delete(a, "cfg");
            Assert.False(Directory.Exists(Path.Combine(a.Directory, "cfg")));

            // The link is listed, but the file behind it is server B's: its rights decide, and A has none on it.
            Assert.Contains(files.List(a, ServerFilePath.Root).Entries, e => e.Name == "stolen.cfg");
            Assert.Equal(FileErrors.AccessDenied, Code(() => files.Delete(a, "stolen.cfg")));
            Assert.Equal("server b", File.ReadAllText(Path.Combine(b.Directory, "secret.cfg")));

            // A backup of A reads as A: the link is left out (counted), and a restore writes as A too.
            File.WriteAllText(Path.Combine(a.Directory, "server.cfg"), "server a");
            var store = new BackupStore(files, NullLogger<BackupStore>.Instance);
            var location = new BackupLocation(Path.Combine(Root, "backups"), shortIds[0], Guid.NewGuid());
            var backup = await store.CreateAsync(location, a, rules: null, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(location.Archive))
            {
                Assert.Equal(["server.cfg"], archive.Entries.Select(e => e.FullName));
            }

            Assert.Equal(1, backup.SkippedFiles);
            File.WriteAllText(Path.Combine(a.Directory, "server.cfg"), "changed");
            await store.RestoreAsync(location, a, backup.Sha256, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
            Assert.Equal("server a", File.ReadAllText(Path.Combine(a.Directory, "server.cfg")));
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
