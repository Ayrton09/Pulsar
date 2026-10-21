using System.Text;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
#if WINDOWS_TESTS
using ContractFact = Pulsar.Agent.Windows.Tests.WindowsFactAttribute;
#else
using ContractFact = Xunit.FactAttribute;
#endif

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// What every file manager implementation does (docs/architecture/08, "File manager"). Shared by the portable
/// implementation (Pulsar.Agent.Core.Tests) and the Windows one (Pulsar.Agent.Windows.Tests links this file).
/// </summary>
public abstract partial class ServerFileManagerContract : IDisposable
{
    protected ServerFileManagerContract()
    {
        Root = Path.Combine(Path.GetTempPath(), "pulsar-file-manager-tests", Guid.NewGuid().ToString("N"));
        ServerDirectory = Directory.CreateDirectory(Path.Combine(Root, "server")).FullName;
        Server = new ProvisionedServer(ServerDirectory, ProcessIdentity.CurrentUser);
    }

    protected string Root { get; }

    protected string ServerDirectory { get; }

    protected ProvisionedServer Server { get; }

    protected abstract IServerFileManager Files { get; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(Root))
        {
            DeleteRoot();
        }
    }

    protected virtual void DeleteRoot()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }

    [ContractFact]
    public void ListsDirectoriesFirstThenEverythingElseByName()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "b"));
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "A"));
        File.WriteAllText(Path.Combine(ServerDirectory, "z.cfg"), "abc");
        File.WriteAllText(Path.Combine(ServerDirectory, "a.txt"), "");

        var listing = Files.List(Server, ServerFilePath.Root);

        Assert.False(listing.Truncated);
        Assert.Equal(["A", "b", "a.txt", "z.cfg"], listing.Entries.Select(e => e.Name));
        Assert.Equal([FileEntryKind.Directory, FileEntryKind.Directory, FileEntryKind.File, FileEntryKind.File], listing.Entries.Select(e => e.Kind));
        Assert.Equal(3, listing.Entries[3].Size);
        Assert.InRange(listing.Entries[3].ModifiedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [ContractFact]
    public void ListingSomethingThatIsNotAFolderFails()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "x");

        Assert.Equal(FileErrors.NotFound, Code(() => Files.List(Server, "missing")));
        Assert.Equal(FileErrors.NotADirectory, Code(() => Files.List(Server, "server.cfg")));
    }

    [ContractFact]
    public void CreatesDirectoriesWithTheirParentsButNeverTwice()
    {
        Files.CreateDirectory(Server, "addons/sourcemod/configs");
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "x");

        Assert.True(Directory.Exists(Path.Combine(ServerDirectory, "addons", "sourcemod", "configs")));
        Assert.Equal(FileErrors.Exists, Code(() => Files.CreateDirectory(Server, "addons/sourcemod")));
        Assert.Equal(FileErrors.Exists, Code(() => Files.CreateDirectory(Server, "server.cfg")));
        Assert.Equal(FileErrors.NotADirectory, Code(() => Files.CreateDirectory(Server, "server.cfg/x")));
    }

    [ContractFact]
    public void RenamesAndMovesWithoutEverReplacing()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "mine");
        File.WriteAllText(Path.Combine(ServerDirectory, "other.cfg"), "other");

        Files.Move(Server, "server.cfg", "cfg/server.cfg");
        Files.Move(Server, "cfg", "config");

        Assert.Equal("mine", File.ReadAllText(Path.Combine(ServerDirectory, "config", "server.cfg")));
        Assert.Equal(FileErrors.Exists, Code(() => Files.Move(Server, "other.cfg", "config/server.cfg")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(ServerDirectory, "config", "server.cfg")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Move(Server, "config", "config/inside")));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Move(Server, "missing.cfg", "x.cfg")));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Move(Server, "other.cfg", "missing/other.cfg")));
    }

    [ContractFact]
    public void DeletesFilesAndWholeDirectoriesReadOnlyOrNot()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "addons", "plugins", "disabled"));
        File.WriteAllText(Path.Combine(ServerDirectory, "addons", "plugins", "a.smx"), "a");
        File.WriteAllText(Path.Combine(ServerDirectory, "addons", "plugins", "disabled", "b.smx"), "b");
        File.SetAttributes(Path.Combine(ServerDirectory, "addons", "plugins", "a.smx"), FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "x");

        Files.Delete(Server, "server.cfg");
        Files.Delete(Server, "addons");

        Assert.Empty(Directory.EnumerateFileSystemEntries(ServerDirectory));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Delete(Server, "addons")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Delete(Server, ServerFilePath.Root)));
    }

    [ContractFact]
    public void AFolderWithMoreEntriesThanAListingHoldsIsListedInOrderAndDeletedWhole()
    {
        var logs = Directory.CreateDirectory(Path.Combine(ServerDirectory, "logs")).FullName;
        var count = FileLimits.MaxListEntries + 500;
        for (var i = count - 1; i >= 0; i--)
        {
            File.WriteAllBytes(Path.Combine(logs, $"log-{i:D5}.txt"), []);
        }

        Directory.CreateDirectory(Path.Combine(logs, "zz-archive"));

        var listing = Files.List(Server, "logs");

        Assert.True(listing.Truncated);
        Assert.Equal(FileLimits.MaxListEntries, listing.Entries.Count);
        Assert.Equal("zz-archive", listing.Entries[0].Name);
        Assert.Equal(Enumerable.Range(0, FileLimits.MaxListEntries - 1).Select(i => $"log-{i:D5}.txt"), listing.Entries.Skip(1).Select(e => e.Name));

        Files.Delete(Server, "logs");

        Assert.False(Directory.Exists(logs));
    }

    [ContractFact]
    public void ReadsFilesUpToALimitWithTheVersionTheListingShows()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "hostname x");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));

        using (var opened = Files.OpenRead(Server, "server.cfg", maxSize: 100))
        {
            Assert.Equal("hostname x", new StreamReader(opened.Content).ReadToEnd());
            var listed = Files.List(Server, ServerFilePath.Root).Entries.Single(e => e.Name == "server.cfg");
            Assert.Equal(listed, opened.Info.Entry);
            Assert.Equal(FileVersion.Of(listed.Size, listed.ModifiedAt), opened.Info.Version);
        }

        Assert.Equal(FileErrors.TooLarge, Code(() => Files.OpenRead(Server, "server.cfg", maxSize: 5)));
        Assert.Equal(FileErrors.IsADirectory, Code(() => Files.OpenRead(Server, "cfg", maxSize: 100)));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.OpenRead(Server, "missing.cfg", maxSize: 100)));
    }

    [ContractFact]
    public void ANewFileAppearsOnlyWhenComplete()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        var target = Path.Combine(ServerDirectory, "cfg", "server.cfg");

        FileEntryResult written;
        using (var pending = Files.Create(Server, "cfg/server.cfg", 4, overwrite: false, expectedVersion: null))
        {
            pending.Content.Write("ab"u8);
            Assert.False(File.Exists(target));
            pending.Content.Write("cd"u8);
            written = pending.Commit();
        }

        Assert.Equal("abcd", File.ReadAllText(target));
        Assert.Equal(new FileEntry("server.cfg", FileEntryKind.File, 4, written.Entry.ModifiedAt), written.Entry);
        Assert.Equal(written.Version, Version("cfg/server.cfg"));
        Assert.Equal(["server.cfg"], Directory.EnumerateFileSystemEntries(Path.Combine(ServerDirectory, "cfg")).Select(Path.GetFileName));
    }

    [ContractFact]
    public void ReplacingAFileFollowsTheRules()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "old");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        var version = Version("server.cfg");

        Assert.Equal(FileErrors.Exists, Code(() => Files.Create(Server, "server.cfg", 1, overwrite: false, expectedVersion: null)));
        Assert.Equal(FileErrors.IsADirectory, Code(() => Files.Create(Server, "cfg", 1, overwrite: true, expectedVersion: null)));
        Assert.Equal(FileErrors.Changed, Code(() => Files.Create(Server, "server.cfg", 1, overwrite: false, expectedVersion: "1-2")));
        Assert.Equal(FileErrors.Changed, Code(() => Files.Create(Server, "new.cfg", 1, overwrite: false, expectedVersion: version)));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Create(Server, "missing/new.cfg", 1, overwrite: false, expectedVersion: null)));

        using (var pending = Files.Create(Server, "server.cfg", 3, overwrite: false, expectedVersion: version))
        {
            pending.Content.Write("new"u8);
            pending.Commit();
        }

        Assert.Equal("new", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));

        using (var pending = Files.Create(Server, "server.cfg", 1, overwrite: true, expectedVersion: null))
        {
            pending.Content.Write("x"u8);
            pending.Commit();
        }

        Assert.Equal("x", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));
    }

    [ContractFact]
    public void AFileChangedDuringTheTransferIsNotOverwritten()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "old");
        var version = Version("server.cfg");

        using var pending = Files.Create(Server, "server.cfg", 3, overwrite: false, expectedVersion: version);
        pending.Content.Write("new"u8);
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "the game wrote this");

        Assert.Equal(FileErrors.Changed, Code(() => pending.Commit()));
        Assert.Equal("the game wrote this", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));
    }

    [ContractFact]
    public void AnIncompleteTransferLeavesNothingBehind()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "old");

        using (var pending = Files.Create(Server, "server.cfg", 10, overwrite: true, expectedVersion: null))
        {
            pending.Content.Write("new"u8);
            Assert.Equal(FileErrors.TransferFailed, Code(() => pending.Commit()));
        }

        using (Files.Create(Server, "other.cfg", 10, overwrite: false, expectedVersion: null))
        {
            // Abandoned without writing anything.
        }

        Assert.Equal("old", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));
        Assert.Equal(["server.cfg"], Directory.EnumerateFileSystemEntries(ServerDirectory).Select(Path.GetFileName));
    }

    [ContractFact]
    public void EveryOperationChecksItsPaths()
    {
        File.WriteAllText(Path.Combine(Root, "outside.cfg"), "secret");

        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.List(Server, "..")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.OpenRead(Server, "../outside.cfg", 100)));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Create(Server, "../x.cfg", 1, overwrite: true, expectedVersion: null)));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.CreateDirectory(Server, "C:/x")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Move(Server, "../outside.cfg", "x.cfg")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Move(Server, "x.cfg", "../x.cfg")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Delete(Server, "../outside.cfg")));
        Assert.True(File.Exists(Path.Combine(Root, "outside.cfg")));
    }

    [ContractFact]
    public void AServerWithoutItsDirectoryHasNoFiles()
    {
        var missing = new ProvisionedServer(Path.Combine(Root, "missing"), ProcessIdentity.CurrentUser);

        Assert.Equal(FileErrors.NotFound, Code(() => Files.List(missing, ServerFilePath.Root)));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Create(missing, "x.cfg", 1, overwrite: false, expectedVersion: null)));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Walk(missing)));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.EnsureDirectory(missing, "cfg")));
    }

    [ContractFact]
    public void WalksTheWholeTreeParentsBeforeTheirChildren()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "game", "csgo", "cfg"));
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "empty"));
        File.WriteAllText(Path.Combine(ServerDirectory, "game", "csgo", "cfg", "server.cfg"), "hostname x");
        File.WriteAllText(Path.Combine(ServerDirectory, "game", "csgo", "gameinfo.gi"), "");
        File.WriteAllText(Path.Combine(ServerDirectory, "top.txt"), "abc");

        var entries = Files.Walk(Server);

        Assert.Equal(
            ["empty", "game", "game/csgo", "game/csgo/cfg", "game/csgo/cfg/server.cfg", "game/csgo/gameinfo.gi", "top.txt"],
            entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        var config = entries.Single(e => e.Path == "game/csgo/cfg/server.cfg");
        Assert.Equal(FileEntryKind.File, config.Kind);
        Assert.Equal(10, config.Size);
        Assert.Equal(FileEntryKind.Directory, entries.Single(e => e.Path == "game/csgo").Kind);
        var paths = entries.Select(e => e.Path).ToList();
        foreach (var path in paths.Where(p => p.Contains('/', StringComparison.Ordinal)))
        {
            Assert.InRange(paths.IndexOf(ServerFilePath.Parent(path)), 0, paths.IndexOf(path) - 1);
        }
    }

    [ContractFact]
    public void AFolderNestedDeeperThanAWalkGoesIsLeftOutAndCountedNotAFailure()
    {
        // A game can nest folders as deep as it likes: backups and clean restores still work, without what is too deep.
        var deep = ServerDirectory;
        for (var level = 0; level <= ServerFileRules.MaxWalkDepth + 1; level++)
        {
            deep = Path.Combine(deep, "d");
        }

        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(ServerDirectory, "top.cfg"), "x");

        var walk = Files.Walk(Server);

        Assert.Contains(walk, e => e.Path == "top.cfg");
        Assert.Equal(ServerFileRules.MaxWalkDepth, walk.Count(e => e.Kind == FileEntryKind.Directory));
        Assert.Equal(1, walk.Refused);
    }

    [ContractFact]
    public void WalkReportsTheNamesItCannotNameInsteadOfDroppingThemSilently()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows names a file with a trailing dot or a device name through \\\\?\\ paths.");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        File.WriteAllText(Path.Combine(ServerDirectory, "cfg", "server.cfg"), "x");
        var extended = @"\\?\" + ServerDirectory;
        File.WriteAllText(extended + @"\cfg\trailing.", "x");
        File.WriteAllText(extended + @"\nul.txt", "x");
        Directory.CreateDirectory(extended + @"\bad.");
        File.WriteAllText(extended + @"\bad.\inside.txt", "x");

        var walk = Files.Walk(Server);

        Assert.Equal(["cfg", "cfg/server.cfg"], walk.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3, walk.Refused);
        Assert.Equal(["bad.", "cfg/trailing.", "nul.txt"], walk.RefusedSample.Order(StringComparer.Ordinal));

        // The test's cleanup cannot name these either.
        File.Delete(extended + @"\cfg\trailing.");
        File.Delete(extended + @"\nul.txt");
        Directory.Delete(extended + @"\bad.", recursive: true);
    }

    [ContractFact]
    public void ListingCountsTheNamesThePanelCannotAddressAndTheBrokenOnesNeverReachJson()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows names a file with a trailing dot, a device name or a lone surrogate through \\\\?\\ paths.");
        File.WriteAllText(Path.Combine(ServerDirectory, "plain.cfg"), "x");
        var extended = @"\\?\" + ServerDirectory;
        string[] odd = ["trailing.", "nul.txt", "bad" + (char)0xD800 + "name.cfg"];
        foreach (var name in odd)
        {
            File.WriteAllText(extended + @"\" + name, "x");
        }

        try
        {
            var listing = Files.List(Server, ServerFilePath.Root);

            // JSON writes a lone surrogate as U+FFFD, a name the panel would accept and open as another file.
            var sent = Assert.IsType<FileListResult>(ProtocolJson.Deserialize<CommandAck>(
                ProtocolJson.Serialize(new CommandAck(Guid.NewGuid(), AckStatus.Accepted, null, null, listing))).Result);
            Assert.Equal(["plain.cfg"], sent.Entries.Select(e => e.Name));
            Assert.Equal(3, sent.Skipped);
            Assert.False(sent.Truncated);
        }
        finally
        {
            foreach (var name in odd)
            {
                File.Delete(extended + @"\" + name);
            }
        }
    }

    [ContractFact]
    public void AFileWhoseTimeIsBeyondYear9999IsListedWalkedReadReplacedAndDeleted()
    {
        // Any account can set a time NTFS keeps but a DateTime cannot hold; a game's account does it to its own files.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only NTFS keeps a time beyond year 9999.");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        File.WriteAllText(Path.Combine(ServerDirectory, "normal.cfg"), "x");
        var future = Path.Combine(ServerDirectory, "future.cfg");
        File.WriteAllText(future, "abc");
        SetLastWriteFileTime(future, long.MaxValue);
        SetLastWriteFileTime(Path.Combine(ServerDirectory, "cfg"), long.MaxValue);

        var listed = Files.List(Server, ServerFilePath.Root).Entries;
        Assert.Equal(["cfg", "future.cfg", "normal.cfg"], listed.Select(e => e.Name));
        Assert.All(listed.Where(e => e.Name != "normal.cfg"), e => Assert.Equal(9999, e.ModifiedAt.Year));
        Assert.Contains(Files.Walk(Server), e => e.Path == "future.cfg" && e.Size == 3);
        Assert.Empty(Files.List(Server, "cfg").Entries);
        using (var opened = Files.OpenRead(Server, "future.cfg", 100))
        {
            Assert.Equal("abc", new StreamReader(opened.Content).ReadToEnd());
            Assert.Equal(FileVersion.Of(3, opened.Info.Entry.ModifiedAt), opened.Info.Version);
        }

        using (var replacement = Files.Create(Server, "future.cfg", 2, overwrite: true, expectedVersion: null))
        {
            replacement.Content.Write("de"u8);
            replacement.Commit();
        }

        Assert.Equal("de", File.ReadAllText(future));
        SetLastWriteFileTime(future, long.MaxValue);
        Files.Delete(Server, "future.cfg");
        Assert.False(File.Exists(future));
        Files.Delete(Server, "cfg");
        Assert.False(Directory.Exists(Path.Combine(ServerDirectory, "cfg")));
    }

    [ContractFact]
    public void EnsuringADirectoryCreatesWhatIsMissingAndKeepsWhatIsThere()
    {
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "addons"));
        File.WriteAllText(Path.Combine(ServerDirectory, "addons", "keep.txt"), "x");
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "x");

        Files.EnsureDirectory(Server, "addons/sourcemod/configs");
        Files.EnsureDirectory(Server, "addons/sourcemod/configs");
        Files.EnsureDirectory(Server, "addons");

        Assert.True(Directory.Exists(Path.Combine(ServerDirectory, "addons", "sourcemod", "configs")));
        Assert.True(File.Exists(Path.Combine(ServerDirectory, "addons", "keep.txt")));
        Assert.Equal(FileErrors.NotADirectory, Code(() => Files.EnsureDirectory(Server, "server.cfg")));
        Assert.Equal(FileErrors.NotADirectory, Code(() => Files.EnsureDirectory(Server, "server.cfg/x")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.EnsureDirectory(Server, "../x")));
    }

    [ContractFact]
    public void TheLentSteamLoginIsOutOfReachButTheRestOfSteamCmdsFolderIsNot()
    {
        var config = Path.Combine(ServerDirectory, ".pulsar", "steamcmd", "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "config.vdf"), "a cached login");
        File.WriteAllText(Path.Combine(ServerDirectory, ".pulsar", "steamcmd", "steamcmd.exe"), "Valve's");
        File.WriteAllText(Path.Combine(ServerDirectory, "planted.vdf"), "x");

        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.OpenRead(Server, ".pulsar/steamcmd/config/config.vdf", 100)));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.OpenRead(Server, ".PULSAR/SteamCMD/Config/config.vdf", 100)));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.List(Server, ".pulsar/steamcmd/config")));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Move(Server, ".pulsar/steamcmd/config/config.vdf", "mine.vdf")));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Move(Server, ".pulsar/steamcmd/config", "mine")));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Move(Server, "planted.vdf", ".pulsar/steamcmd/config/planted.vdf")));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Delete(Server, ".pulsar/steamcmd/config/config.vdf")));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Delete(Server, ".pulsar/steamcmd/config")));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Create(Server, ".pulsar/steamcmd/config/config.vdf", 1, overwrite: true, expectedVersion: null)));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.CreateDirectory(Server, ".pulsar/steamcmd/config/sub")));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.EnsureDirectory(Server, ".pulsar/steamcmd/config/sub")));
        Assert.Equal("a cached login", File.ReadAllText(Path.Combine(config, "config.vdf")));

        // The rest is Pulsar's to use: listed, walked (never into the login) and deleted whole when SteamCMD is rebuilt.
        Assert.Contains(Files.List(Server, ".pulsar/steamcmd").Entries, e => e.Name == "config");
        var walked = Files.Walk(Server).Select(e => e.Path).ToList();
        Assert.Contains(".pulsar/steamcmd/steamcmd.exe", walked);
        Assert.DoesNotContain(".pulsar/steamcmd/config/config.vdf", walked);
        Files.Delete(Server, ".pulsar/steamcmd");
        Assert.False(Directory.Exists(Path.Combine(ServerDirectory, ".pulsar", "steamcmd")));
    }

    [ContractFact]
    public void AJunctionIsListedAsALinkAndNeverFollowedWhenItIsTheListedFolder()
    {
        // Needs no elevation (a symbolic link does); only the entry itself is the link, so the ancestors are all clean.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are a Windows feature.");
        }

        var outside = Directory.CreateDirectory(Path.Combine(Root, "outside-of-the-server")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.cfg"), "rcon_password other-server");
        var junction = Path.Combine(ServerDirectory, "alias");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", junction, outside])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            mklink.WaitForExit();
            Assert.True(mklink.ExitCode == 0, mklink.StandardError.ReadToEnd());
        }

        try
        {
            Assert.Equal(FileEntryKind.Link, Files.List(Server, ServerFilePath.Root).Entries.Single(e => e.Name == "alias").Kind);
            Assert.Equal(FileErrors.Link, Code(() => Files.List(Server, "alias")));
        }
        finally
        {
            Directory.Delete(junction); // the link itself, never what it points to
        }

        Assert.True(File.Exists(Path.Combine(outside, "secret.cfg")));
    }

    protected string Version(string path)
    {
        using var opened = Files.OpenRead(Server, path, long.MaxValue);
        return opened.Info.Version;
    }

    protected static string Code(Action action) => Assert.Throws<FileManagerException>(action).Code;

    protected static string Code(Func<object?> action) => Assert.Throws<FileManagerException>(() => action()).Code;

    protected static string Text(OpenedFile opened)
    {
        using (opened)
        {
            return new StreamReader(opened.Content, Encoding.UTF8).ReadToEnd();
        }
    }

    /// <summary>Sets the last-write FILETIME (100 ns since 1601) of a file or folder, beyond what a DateTime holds if asked.</summary>
    protected static void SetLastWriteFileTime(string path, long fileTime)
    {
        // FILE_WRITE_ATTRIBUTES, all sharing, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (to open a folder).
        using var handle = CreateFile(path, 0x100, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref fileTime))
        {
            throw new IOException($"Could not set the time of '{path}' (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr creation, IntPtr access, ref long write);
}
