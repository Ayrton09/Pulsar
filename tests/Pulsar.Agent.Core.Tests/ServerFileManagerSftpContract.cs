using Pulsar.Agent.Core.Files;
using Pulsar.Contracts;
#if WINDOWS_TESTS
using ContractFact = Pulsar.Agent.Windows.Tests.WindowsFactAttribute;
#else
using ContractFact = Xunit.FactAttribute;
#endif

namespace Pulsar.Agent.Core.Tests;

/// <summary>What every file manager does for SFTP (Phase 17): pages, stat, times, renames that replace, streamed and appended writes.</summary>
public abstract partial class ServerFileManagerContract
{
    [ContractFact]
    public void PagesListEveryEntryOnceByNameAndHideWhatSftpHides()
    {
        var names = Enumerable.Range(0, FileLimits.MaxListEntries + 50).Select(i => $"map{i:D4}.bsp").ToList();
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(ServerDirectory, name), "");
        }

        Directory.CreateDirectory(Path.Combine(ServerDirectory, "Cfg"));
        LendSteamLogin();
        File.WriteAllText(Path.Combine(ServerDirectory, "odd\u202Egnp.exe"), "");

        var first = Files.ListPage(Server, ServerFilePath.Root, null);
        Assert.Equal(FileLimits.MaxListEntries, first.Entries.Count);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(first.Entries[^1].Name, first.Next);
        var second = Files.ListPage(Server, ServerFilePath.Root, first.Next);
        Assert.Null(second.Next);
        Assert.Equal(0, second.Skipped);

        var expected = names.Append("Cfg").Order(FileNameOrder.Comparer);
        Assert.Equal(expected, first.Entries.Concat(second.Entries).Select(e => e.Name));
        Assert.Equal(FileEntryKind.Directory, first.Entries.Concat(second.Entries).Single(e => e.Name == "Cfg").Kind);
        Assert.False(first.Truncated || second.Truncated);

        // A page is a listing like any other: same checks, same refusals.
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "x");
        Assert.Equal(FileErrors.NotFound, Code(() => Files.ListPage(Server, "missing", null)));
        Assert.Equal(FileErrors.NotADirectory, Code(() => Files.ListPage(Server, "server.cfg", null)));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.ListPage(Server, "..", null)));
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.ListPage(Server, ".pulsar/steamcmd/config", null)));
    }

    [ContractFact]
    public void StatDescribesOneEntryAsTheListingDoes()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "hostname x");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));

        var file = Files.Stat(Server, "server.cfg");
        var listed = Files.List(Server, ServerFilePath.Root).Entries.Single(e => e.Name == "server.cfg");
        Assert.Equal(listed, file.Entry);
        Assert.Equal(Version("server.cfg"), file.Version);
        Assert.Equal(FileEntryKind.Directory, Files.Stat(Server, "cfg").Entry.Kind);

        Assert.Equal(FileErrors.NotFound, Code(() => Files.Stat(Server, "missing.cfg")));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Stat(Server, "missing/x.cfg")));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Stat(Server, ServerFilePath.Root)));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Stat(Server, "../outside.cfg")));
        LendSteamLogin();
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Stat(Server, ".pulsar/steamcmd/config/config.vdf")));
    }

    [ContractFact]
    public void TheModificationTimeOfAFileOrFolderCanBeSetWithinWhatTheFileSystemKeeps()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "x");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        var before = Version("server.cfg");
        var time = new DateTimeOffset(2020, 5, 17, 10, 30, 0, TimeSpan.FromHours(2));

        var set = Files.SetModified(Server, "server.cfg", time);
        Assert.Equal(time, set.Entry.ModifiedAt);
        Assert.Equal(time.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(ServerDirectory, "server.cfg")));
        Assert.NotEqual(before, set.Version);
        Assert.Equal(set.Version, Version("server.cfg"));
        Assert.Equal("x", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));

        Assert.Equal(time, Files.SetModified(Server, "cfg", time).Entry.ModifiedAt);
        Assert.Equal(time.UtcDateTime, Directory.GetLastWriteTimeUtc(Path.Combine(ServerDirectory, "cfg")));

        // Times no file system keeps are clamped, not refused: a client's odd value must not fail its upload.
        Assert.Equal(1601, Files.SetModified(Server, "server.cfg", DateTimeOffset.MinValue).Entry.ModifiedAt.Year);
        Assert.Equal(9999, Files.SetModified(Server, "server.cfg", DateTimeOffset.MaxValue).Entry.ModifiedAt.Year);

        Assert.Equal(FileErrors.NotFound, Code(() => Files.SetModified(Server, "missing.cfg", time)));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.SetModified(Server, "../outside.cfg", time)));
        LendSteamLogin();
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.SetModified(Server, ".pulsar/steamcmd/config", time)));
    }

    [ContractFact]
    public void ARenameMayReplaceAFileButNothingElse()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "old");
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg.filepart"), "new");
        File.WriteAllText(Path.Combine(ServerDirectory, "other.cfg"), "other");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "maps"));

        Assert.Equal(FileErrors.Exists, Code(() => Files.Move(Server, "server.cfg.filepart", "server.cfg")));
        Files.Move(Server, "server.cfg.filepart", "server.cfg", replace: true);
        Assert.Equal("new", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));
        Assert.False(File.Exists(Path.Combine(ServerDirectory, "server.cfg.filepart")));

        Assert.Equal(FileErrors.IsADirectory, Code(() => Files.Move(Server, "other.cfg", "cfg", replace: true)));
        Assert.Equal(FileErrors.Exists, Code(() => Files.Move(Server, "maps", "other.cfg", replace: true)));
        Assert.Equal(FileErrors.IsADirectory, Code(() => Files.Move(Server, "maps", "cfg", replace: true)));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Move(Server, "missing.cfg", "server.cfg", replace: true)));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Move(Server, "cfg", "cfg/inside", replace: true)));
        LendSteamLogin();
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Move(Server, "other.cfg", ".pulsar/steamcmd/config/x", replace: true)));

        // Nothing there: an ordinary rename, folders included.
        Files.Move(Server, "maps", "maps2", replace: true);
        Assert.True(Directory.Exists(Path.Combine(ServerDirectory, "maps2")));
        Assert.Equal("other", File.ReadAllText(Path.Combine(ServerDirectory, "other.cfg")));
        Assert.True(Directory.Exists(Path.Combine(ServerDirectory, "cfg")));
    }

    [ContractFact]
    public void AStreamedWriteTakesWhatArrivedWithinItsLimitAndOnlyOnceCommitted()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "server.cfg"), "old");

        using (var pending = Files.Create(Server, "new.cfg", 100, overwrite: false, expectedVersion: null, sizeIsLimit: true))
        {
            pending.Content.Write("abc"u8);
            Assert.False(File.Exists(Path.Combine(ServerDirectory, "new.cfg")));
            Assert.Equal(3, pending.Commit().Entry.Size);
        }

        Assert.Equal("abc", File.ReadAllText(Path.Combine(ServerDirectory, "new.cfg")));

        using (var pending = Files.Create(Server, "server.cfg", 100, overwrite: true, expectedVersion: null, sizeIsLimit: true))
        {
            pending.Content.Write("replaced"u8);
            pending.Commit();
        }

        Assert.Equal("replaced", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));

        using (var pending = Files.Create(Server, "server.cfg", 100, overwrite: true, expectedVersion: null, sizeIsLimit: true))
        {
            pending.Content.Write("never committed"u8);
        }

        Assert.Equal("replaced", File.ReadAllText(Path.Combine(ServerDirectory, "server.cfg")));
        Assert.Equal(["new.cfg", "server.cfg"], Directory.EnumerateFileSystemEntries(ServerDirectory).Select(Path.GetFileName).Order());
    }

    [ContractFact]
    public void AnAppendWritesAtTheEndInPlaceAndKeepsWhatArrived()
    {
        File.WriteAllText(Path.Combine(ServerDirectory, "map.bsp"), "abc");
        Directory.CreateDirectory(Path.Combine(ServerDirectory, "cfg"));
        var version = Version("map.bsp");

        using (var pending = Files.Append(Server, "map.bsp", version))
        {
            pending.Content.Write("def"u8);
            var appended = pending.Commit();
            Assert.Equal(6, appended.Entry.Size);
            Assert.Equal("map.bsp", appended.Entry.Name);
        }

        Assert.Equal("abcdef", File.ReadAllText(Path.Combine(ServerDirectory, "map.bsp")));

        // An interrupted append keeps what arrived: the client resumes from there.
        using (var pending = Files.Append(Server, "map.bsp", expectedVersion: null))
        {
            pending.Content.Write("gh"u8);
            pending.Content.Flush();
        }

        Assert.Equal("abcdefgh", File.ReadAllText(Path.Combine(ServerDirectory, "map.bsp")));

        using (var pending = Files.Append(Server, "cfg/new.cfg", expectedVersion: null))
        {
            pending.Content.Write("x"u8);
            pending.Commit();
        }

        Assert.Equal("x", File.ReadAllText(Path.Combine(ServerDirectory, "cfg", "new.cfg")));

        Assert.Equal(FileErrors.Changed, Code(() => Files.Append(Server, "map.bsp", version)));
        Assert.Equal(FileErrors.Changed, Code(() => Files.Append(Server, "missing.bsp", version)));
        Assert.Equal(FileErrors.IsADirectory, Code(() => Files.Append(Server, "cfg", expectedVersion: null)));
        Assert.Equal(FileErrors.NotFound, Code(() => Files.Append(Server, "missing/new.cfg", expectedVersion: null)));
        Assert.Equal(FileErrors.InvalidPath, Code(() => Files.Append(Server, "../outside.cfg", expectedVersion: null)));
        LendSteamLogin();
        Assert.Equal(FileErrors.AccessDenied, Code(() => Files.Append(Server, ".pulsar/steamcmd/config/config.vdf", expectedVersion: null)));
        Assert.Equal(["map.bsp"], Directory.EnumerateFiles(ServerDirectory).Select(Path.GetFileName));
    }

    [ContractFact]
    public void TheNewOperationsNeverFollowAJunction()
    {
        // Needs no elevation (a symbolic link does).
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions are a Windows feature.");
        }

        var outside = Directory.CreateDirectory(Path.Combine(Root, "outside-of-the-server")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.cfg"), "rcon_password other-server");
        var before = File.GetLastWriteTimeUtc(Path.Combine(outside, "secret.cfg"));
        File.WriteAllText(Path.Combine(ServerDirectory, "planted.cfg"), "planted");
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
            Assert.Equal(FileEntryKind.Link, Files.Stat(Server, "alias").Entry.Kind);
            Assert.Equal(FileErrors.Link, Code(() => Files.ListPage(Server, "alias", null)));
            Assert.Contains(Code(() => Files.Stat(Server, "alias/secret.cfg")), s_refusals);
            Assert.Equal(FileErrors.Link, Code(() => Files.SetModified(Server, "alias", DateTimeOffset.UnixEpoch)));
            Assert.Contains(Code(() => Files.SetModified(Server, "alias/secret.cfg", DateTimeOffset.UnixEpoch)), s_refusals);
            Assert.Equal(FileErrors.Link, Code(() => Files.Append(Server, "alias", expectedVersion: null)));
            Assert.Equal(FileErrors.Link, Code(() => Files.Append(Server, "alias/secret.cfg", expectedVersion: null)));
            Assert.Equal(FileErrors.Link, Code(() => Files.Move(Server, "planted.cfg", "alias", replace: true)));
            Assert.Equal(FileErrors.Link, Code(() => Files.Move(Server, "planted.cfg", "alias/secret.cfg", replace: true)));
        }
        finally
        {
            Directory.Delete(junction); // the link itself, never what it points to
        }

        Assert.Equal("rcon_password other-server", File.ReadAllText(Path.Combine(outside, "secret.cfg")));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(outside, "secret.cfg")));
    }

    /// <summary>
    /// How a path through a junction is refused: by name before anything is opened (the portable implementation), or by
    /// where the opened handle really is (the Windows one).
    /// </summary>
    private static readonly string[] s_refusals = [FileErrors.Link, FileErrors.OutsideServer];

    /// <summary>The Steam login an install lends SteamCMD, where every implementation must refuse to go.</summary>
    private void LendSteamLogin()
    {
        var config = Directory.CreateDirectory(Path.Combine(ServerDirectory, ".pulsar", "steamcmd", "config")).FullName;
        File.WriteAllText(Path.Combine(config, "config.vdf"), "a cached login");
    }
}
