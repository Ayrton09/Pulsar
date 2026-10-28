using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Backups;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>Backups on the node (docs/architecture/08, "Backups and restore"): what goes in, and what a restore may write.</summary>
public sealed class BackupStoreTests : IDisposable
{
    private static readonly BackupDefinition s_rules = new(["cfg/**", "addons/**", "MOTD.txt"], ["**/*.log"]);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly string _serverDirectory;
    private readonly ProvisionedServer _server;
    private readonly BackupStore _store = new(new DirectServerFileManager(), NullLogger<BackupStore>.Instance);
    private readonly List<(string Stage, double? Percent)> _progress = [];

    public BackupStoreTests()
    {
        _serverDirectory = Directory.CreateDirectory(Path.Combine(_root, "server")).FullName;
        _server = new ProvisionedServer(_serverDirectory, ProcessIdentity.CurrentUser);
        BackupsRoot = Path.Combine(_root, "backups");
    }

    private string BackupsRoot { get; }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AFileTheGameLocksIsLeftOutOfALiveBackupAndCounted()
    {
        // A running Minecraft read live (no snapshot): world/session.lock opens, but its bytes are locked.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Byte-range locks keep other handles from reading only on Windows.");
        Write("cfg/server.cfg", "hostname");
        Write("cfg/session.lock", "locked");
        using var game = new FileStream(Path.Combine(_serverDirectory, "cfg", "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (OperatingSystem.IsWindows())
        {
            game.Lock(0, game.Length);
        }
        var location = Location();

        var result = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);

        Assert.Equal(["cfg/server.cfg"], Entries(location.Archive));
        Assert.Equal((1, 1), (result.FileCount, result.SkippedFiles));
    }

    [Fact]
    public async Task AnotherNodesArchiveIsKeptOnlyWhenItIsExactlyTheBackupItMade()
    {
        // A clone's (Pulsar 1.0.16): the archive the panel relays from the other node.
        Write("cfg/server.cfg", "hostname");
        var made = Location();
        var result = await _store.CreateAsync(made, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        var archive = await File.ReadAllBytesAsync(made.Archive, TestContext.Current.CancellationToken);
        var received = new BackupLocation(BackupsRoot, "other1", Guid.NewGuid());
        Task Send(byte[] bytes, Stream output, CancellationToken token) => output.WriteAsync(bytes, token).AsTask();

        // Another archive of the same size, or a cut one: refused, nothing kept.
        var changed = (byte[])archive.Clone();
        changed[^30] ^= 0xFF;
        foreach (var wrong in new[] { changed, archive[..^1] })
        {
            await Assert.ThrowsAsync<BackupException>(() => _store.ReceiveAsync(received, (output, token) => Send(wrong, output, token), result.Sha256,
                result.Size, Progress, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(received.Archive));
            Assert.False(File.Exists(received.Partial));
        }

        await _store.ReceiveAsync(received, (output, token) => Send(archive, output, token), result.Sha256, result.Size, Progress,
            TestContext.Current.CancellationToken);
        Assert.Equal(archive, await File.ReadAllBytesAsync(received.Archive, TestContext.Current.CancellationToken));
        // Marked as another node's: every restore of it deletes by this server's own rules only.
        Assert.True(File.Exists(received.ReceivedMarker));
        Assert.True(_store.Delete(received));
        Assert.False(File.Exists(received.ReceivedMarker));
    }

    [Fact]
    public async Task ABackupHoldsWhatTheManifestIncludesAndNothingElse()
    {
        Write("cfg/server.cfg", "hostname \"Pulsar\"");
        Write("cfg/MapCycle.TXT", "de_dust2");
        Write("cfg/console.LOG", "noise");
        Write("addons/sourcemod/plugins/admin.smx", new string('x', 10_000));
        Write("motd.txt", "welcome");
        Write("bin/server.dll", "reinstallable");
        Write("cfg/.pulsar-upload-0123456789abcdef.tmp", "half an upload");
        Write(".pulsar/steamcmd/steamcmd.exe", "the server's own SteamCMD");
        var location = Location();

        var result = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);

        // Globs ignore case, as Windows does; names keep theirs.
        Assert.Equal(["addons/sourcemod/plugins/admin.smx", "cfg/MapCycle.TXT", "cfg/server.cfg", "motd.txt"], Entries(location.Archive));
        Assert.Equal(4, result.FileCount);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Equal(new FileInfo(location.Archive).Length, result.Size);
        Assert.Equal(Hash(location.Archive), result.Sha256);
        Assert.Equal("hostname \"Pulsar\"", Read(location.Archive, "cfg/server.cfg"));
        Assert.False(File.Exists(location.Partial));
        Assert.Equal([BackupStages.Scanning, BackupStages.Archiving, BackupStages.Checking], _progress.Select(p => p.Stage).Distinct());
        Assert.Equal(100, _progress.Where(p => p.Stage == BackupStages.Archiving).Max(p => p.Percent));
    }

    [Fact]
    public async Task FilesThatDoNotCompressAreStoredAsTheyAre()
    {
        var map = RandomNumberGenerator.GetBytes(1 << 20);
        Directory.CreateDirectory(Path.Combine(_serverDirectory, "addons"));
        await File.WriteAllBytesAsync(Path.Combine(_serverDirectory, "addons", "map.vpk"), map, TestContext.Current.CancellationToken);
        Write("cfg/server.cfg", string.Concat(Enumerable.Repeat("sv_cheats 0\n", 50_000)));
        var location = Location();

        await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(location.Archive);
        var stored = zip.GetEntry("addons/map.vpk")!;
        Assert.Equal(stored.Length, stored.CompressedLength); // no deflate overhead on data that does not shrink
        var config = zip.GetEntry("cfg/server.cfg")!;
        Assert.True(config.CompressedLength < config.Length / 20, $"{config.CompressedLength} of {config.Length}");
        await using var content = stored.Open();
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(map, copy.ToArray());
    }

    [Fact]
    public async Task WithoutBackupRulesEverythingButPulsarsOwnFilesGoesIn()
    {
        Write("game/bin/server.dll", "binary");
        Write("server.cfg", "x");
        Write(".pulsar/steamcmd/steamcmd.exe", "SteamCMD");
        Directory.CreateDirectory(Path.Combine(_serverDirectory, "empty"));
        var location = Location();

        var result = await _store.CreateAsync(location, _server, rules: null, Progress, TestContext.Current.CancellationToken);

        Assert.Equal(["game/bin/server.dll", "server.cfg"], Entries(location.Archive));
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public async Task APrivateFileNeverGoesInEvenWithoutBackupRules()
    {
        // GoldSrc's console log holds its RCON password: a manifest without include rules still leaves it out.
        Write("qconsole.log", "rcon 1 \"the password\" status");
        Write("server.cfg", "x");
        var location = Location();

        var result = await _store.CreateAsync(location, _server with { PrivateFiles = ["qconsole.log"] }, rules: null, Progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(["server.cfg"], Entries(location.Archive));
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task ARestorePutsTheBackedUpFilesBackAndLeavesTheRestAlone()
    {
        Write("cfg/server.cfg", "hostname old");
        Write("addons/plugin.smx", "plugin");
        var location = Location();
        var backup = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        Write("cfg/server.cfg", "hostname changed after the backup");
        File.Delete(Path.Combine(_serverDirectory, "addons", "plugin.smx"));
        Directory.Delete(Path.Combine(_serverDirectory, "addons"));
        Write("cfg/new.cfg", "made later");
        _progress.Clear();

        await _store.RestoreAsync(location, _server, backup.Sha256, null, Progress, TestContext.Current.CancellationToken);

        Assert.Equal("hostname old", ReadServer("cfg/server.cfg"));
        Assert.Equal("plugin", ReadServer("addons/plugin.smx"));
        Assert.Equal("made later", ReadServer("cfg/new.cfg"));
        Assert.Equal([BackupStages.Checking, BackupStages.Restoring], _progress.Select(p => p.Stage).Distinct());
        Assert.DoesNotContain(Directory.EnumerateFiles(_serverDirectory, "*", SearchOption.AllDirectories),
            f => Path.GetFileName(f).StartsWith(ServerFileRules.TemporaryPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACleanRestoreDeletesWhatTheBackupWouldHaveTakenButDoesNotHave()
    {
        Write("cfg/server.cfg", "hostname old");
        Write("server.exe", "the game");
        var location = Location();
        var backup = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        Write("cfg/new.cfg", "made later");
        Write("addons/later/plugin.smx", "installed later");
        Write("cfg/console.log", "excluded by the rules");
        Write("game/update.dll", "outside the rules");
        Write(".pulsar/state.json", "Pulsar's own");
        _progress.Clear();

        await _store.RestoreAsync(location, _server, backup.Sha256, new CleanRestore(s_rules), Progress, TestContext.Current.CancellationToken);

        Assert.Equal("hostname old", ReadServer("cfg/server.cfg"));
        Assert.False(File.Exists(Path.Combine(_serverDirectory, "cfg", "new.cfg")));
        Assert.False(File.Exists(Path.Combine(_serverDirectory, "addons", "later", "plugin.smx")));
        Assert.Equal("excluded by the rules", ReadServer("cfg/console.log"));
        Assert.Equal("outside the rules", ReadServer("game/update.dll"));
        Assert.Equal("the game", ReadServer("server.exe"));
        Assert.Equal("Pulsar's own", ReadServer(".pulsar/state.json"));
        Assert.Equal([BackupStages.Checking, BackupStages.Cleaning, BackupStages.Restoring], _progress.Select(p => p.Stage).Distinct());
    }

    [Fact]
    public async Task ACleanRestoreDeletesByTheRulesTheBackupWasMadeWithNotTodaysOnes()
    {
        Write("cfg/server.cfg", "hostname old");
        var location = Location();
        var backup = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        Write("cfg/new.cfg", "made later");
        Write("game/update.dll", "outside the rules the backup was made with");
        var wider = new BackupDefinition(["**/*"]); // a template update widened them since

        await _store.RestoreAsync(location, _server, backup.Sha256, new CleanRestore(wider), Progress, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_serverDirectory, "cfg", "new.cfg")));
        Assert.Equal("outside the rules the backup was made with", ReadServer("game/update.dll"));
        Assert.Equal("hostname old", ReadServer("cfg/server.cfg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"pulsar":2,"include":["**/*"]}""")]
    [InlineData("""{"pulsar":1,"include":["**/*",null]}""")]
    [InlineData("""{"pulsar":1,"include":"**/*"}""")]
    public async Task ACleanRestoreOfAnArchiveThatRecordsNoRulesUsesTheServersCurrentOnes(string? comment)
    {
        // Made before Phase 15 (no comment), or a comment that is not Pulsar's.
        var location = Location();
        Directory.CreateDirectory(location.Directory);
        await using (var stream = File.Create(location.Archive))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await using (var entry = await archive.CreateEntry("cfg/server.cfg").OpenAsync(TestContext.Current.CancellationToken))
            {
                await entry.WriteAsync("hostname old"u8.ToArray(), TestContext.Current.CancellationToken);
            }

            if (comment is not null)
            {
                archive.Comment = comment;
            }
        }

        Write("cfg/new.cfg", "made later");
        Write("game/update.dll", "outside the current rules");

        await _store.RestoreAsync(location, _server, Hash(location.Archive), new CleanRestore(s_rules), Progress,
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_serverDirectory, "cfg", "new.cfg")));
        Assert.Equal("outside the current rules", ReadServer("game/update.dll"));
        Assert.Equal("hostname old", ReadServer("cfg/server.cfg"));
    }

    [Fact]
    public async Task ACleanRestoreSaysSoWhenPartOfTheTreeWasNeverLookedAt()
    {
        // A game nested folders deeper than a walk goes, under what the rules select: whatever is there is not deleted.
        Write("cfg/server.cfg", "hostname old");
        var store = new BackupStore(new DirectServerFileManager(), NullLogger<BackupStore>.Instance);
        var location = Location();
        var backup = await store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        Write(string.Join('/', ["cfg", .. Enumerable.Repeat("d", ServerFileRules.MaxWalkDepth + 1), "hidden.cfg"]), "made later");

        var error = await Assert.ThrowsAsync<BackupException>(() =>
            store.RestoreAsync(location, _server, backup.Sha256, new CleanRestore(s_rules), Progress, TestContext.Current.CancellationToken));

        Assert.Contains("1 newer files could not be deleted (first: cfg/d/", error.Message, StringComparison.Ordinal);
        Assert.Equal("hostname old", ReadServer("cfg/server.cfg"));
    }

    [Fact]
    public async Task AFileACleanRestoreCannotDeleteDoesNotKeepTheBackupFromBeingRestored()
    {
        Write("cfg/server.cfg", "hostname old");
        var files = new HookedServerFileManager(new DirectServerFileManager());
        var store = new BackupStore(files, NullLogger<BackupStore>.Instance);
        var location = Location();
        var backup = await store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        Write("cfg/server.cfg", "hostname changed");
        Write("cfg/held.cfg", "held open by something");
        Write("cfg/new.cfg", "made later");
        files.OnDelete = path =>
        {
            if (path == "cfg/held.cfg")
            {
                throw new FileManagerException(FileErrors.InUse, $"'{path}' is open in another program.");
            }
        };

        var error = await Assert.ThrowsAsync<BackupException>(() =>
            store.RestoreAsync(location, _server, backup.Sha256, new CleanRestore(s_rules), Progress, TestContext.Current.CancellationToken));

        Assert.Equal(FileErrors.InUse, error.Code);
        Assert.Contains("1 newer files could not be deleted (first: cfg/held.cfg:", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_serverDirectory, "cfg", "new.cfg")));
        Assert.Equal("held open by something", ReadServer("cfg/held.cfg"));
        Assert.Equal("hostname old", ReadServer("cfg/server.cfg"));
    }

    [Fact]
    public async Task ACleanRestoreOfAnArchiveThatChangedDeletesNothing()
    {
        Write("cfg/server.cfg", "hostname old");
        var location = Location();
        var backup = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        Write("cfg/new.cfg", "made later");
        await using (var archive = File.OpenWrite(location.Archive))
        {
            archive.Seek(-1, SeekOrigin.End);
            archive.WriteByte(0x42);
        }

        await Assert.ThrowsAsync<BackupException>(() =>
            _store.RestoreAsync(location, _server, backup.Sha256, new CleanRestore(s_rules), Progress, TestContext.Current.CancellationToken));

        Assert.Equal("made later", ReadServer("cfg/new.cfg"));
    }

    [Fact]
    public async Task AnArchiveThatChangedOnDiskIsNotRestored()
    {
        Write("cfg/server.cfg", "hostname old");
        var location = Location();
        var backup = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        Write("cfg/server.cfg", "hostname current");
        await using (var archive = File.OpenWrite(location.Archive))
        {
            archive.Seek(-1, SeekOrigin.End);
            archive.WriteByte(0x42);
        }

        var error = await Assert.ThrowsAsync<BackupException>(() =>
            _store.RestoreAsync(location, _server, backup.Sha256, null, Progress, TestContext.Current.CancellationToken));

        Assert.Equal(BackupErrors.Corrupt, error.Code);
        Assert.Equal("hostname current", ReadServer("cfg/server.cfg"));
    }

    [Theory]
    [InlineData("../outside.cfg")]
    [InlineData("cfg/../../outside.cfg")]
    [InlineData("/outside.cfg")]
    [InlineData("C:/outside.cfg")]
    [InlineData("cfg\\..\\..\\outside.cfg")]
    [InlineData("cfg/server.cfg:hidden")]
    [InlineData("cfg/con.txt")]
    [InlineData(".pulsar/steamcmd/steamcmd.exe")]
    [InlineData(".PULSAR/x")]
    [InlineData("cfg/server.cfg|CFG/SERVER.CFG")]
    public async Task AnArchiveWithAnEntryARestoreMayNotWriteIsRefusedBeforeWritingAnything(string names)
    {
        var location = Location();
        Directory.CreateDirectory(location.Directory);
        await using (var stream = File.Create(location.Archive))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var name in names.Split('|').Prepend("cfg/first.cfg"))
            {
                await using var entry = await archive.CreateEntry(name).OpenAsync(TestContext.Current.CancellationToken);
                await entry.WriteAsync("evil"u8.ToArray(), TestContext.Current.CancellationToken);
            }
        }

        var error = await Assert.ThrowsAsync<BackupException>(() =>
            _store.RestoreAsync(location, _server, Hash(location.Archive), null, Progress, TestContext.Current.CancellationToken));

        Assert.Equal(BackupErrors.Corrupt, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_serverDirectory));
        Assert.False(File.Exists(Path.Combine(_root, "outside.cfg")));
    }

    [Fact]
    public async Task AFolderNamedLikeAShortNameIsBackedUpAndRestored()
    {
        // A game's own folder named like an 8.3 short name is a folder of that name: never .pulsar's, which a walk names by its long name.
        Write("data~1/world.sav", "the world");
        Write(".pulsar/steamcmd/steamcmd.exe", "the server's own SteamCMD");
        var location = Location();

        var made = await _store.CreateAsync(location, _server, null, Progress, TestContext.Current.CancellationToken);

        Assert.Equal(["data~1/world.sav"], Entries(location.Archive));
        Assert.Equal((1, 0), (made.FileCount, made.SkippedFiles));
        Directory.Delete(Path.Combine(_serverDirectory, "data~1"), recursive: true);

        await _store.RestoreAsync(location, _server, made.Sha256, null, Progress, TestContext.Current.CancellationToken);

        Assert.Equal("the world", ReadServer("data~1/world.sav"));
        Assert.Contains("data~1", Directory.GetDirectories(_serverDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AShortNameThatLeadsToAnotherFolderIsNotRestoredThroughButTheRestIs()
    {
        // On a volume that keeps 8.3 names, PULSAR~1 is .pulsar itself: what an archive has there must not land in it.
        Directory.CreateDirectory(Path.Combine(_serverDirectory, ".pulsar"));
        Assert.SkipUnless(Directory.Exists(Path.Combine(_serverDirectory, "PULSAR~1")), "The volume keeps no 8.3 names.");
        var location = Location();
        Directory.CreateDirectory(location.Directory);
        await using (var stream = File.Create(location.Archive))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "cfg/server.cfg", "PULSAR~1/steamcmd/steamcmd.exe" })
            {
                await using var entry = await archive.CreateEntry(name).OpenAsync(TestContext.Current.CancellationToken);
                await entry.WriteAsync("from the backup"u8.ToArray(), TestContext.Current.CancellationToken);
            }
        }

        var error = await Assert.ThrowsAsync<BackupException>(() =>
            _store.RestoreAsync(location, _server, Hash(location.Archive), null, Progress, TestContext.Current.CancellationToken));

        Assert.Equal(FileErrors.Exists, error.Code);
        Assert.Contains("PULSAR~1", error.Message, StringComparison.Ordinal);
        Assert.Equal("from the backup", ReadServer("cfg/server.cfg"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_serverDirectory, ".pulsar")));
    }

    [Fact]
    public async Task ACancelledBackupLeavesNoArchive()
    {
        Write("cfg/a.cfg", "a");
        Write("cfg/b.cfg", "b");
        var location = Location();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.CreateAsync(location, _server, s_rules, async (stage, percent) =>
        {
            if (stage == BackupStages.Archiving)
            {
                await cancellation.CancelAsync();
            }
        }, cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(location.Directory));
    }

    [Fact]
    public async Task ABackupCanBeOpenedForDownloadAndDeletedOnce()
    {
        Write("cfg/server.cfg", "x");
        var location = Location();
        var backup = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);

        using (var opened = _store.Open(location))
        {
            Assert.Equal(backup.Size, opened.Info.Entry.Size);
            Assert.Equal(FileEntryKind.File, opened.Info.Entry.Kind);
            using var copy = new MemoryStream();
            await opened.Content.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(backup.Sha256, Convert.ToHexStringLower(SHA256.HashData(copy.ToArray())));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(BackupErrors.InUse, Assert.Throws<BackupException>(() => _store.Delete(location)).Code);
            }
        }

        Assert.True(_store.Delete(location));
        Assert.False(_store.Delete(location));
        Assert.Equal(BackupErrors.NotFound, Assert.Throws<BackupException>(() => _store.Open(location)).Code);
        Assert.Equal(BackupErrors.NotFound, (await Assert.ThrowsAsync<BackupException>(() =>
            _store.RestoreAsync(location, _server, backup.Sha256, null, Progress, TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task CleaningUpRemovesWhatAnInterruptedBackupLeftAndTheBackupsOfADeletedServer()
    {
        Write("cfg/server.cfg", "x");
        var kept = Location();
        await _store.CreateAsync(kept, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Location().Partial, "half a zip", TestContext.Current.CancellationToken);
        var other = new BackupLocation(BackupsRoot, "other1", Guid.NewGuid());
        Directory.CreateDirectory(other.Directory);
        await File.WriteAllTextAsync(other.Archive, "zip", TestContext.Current.CancellationToken);

        _store.CleanUp(BackupsRoot);
        _store.DeleteAll(BackupsRoot, "other1");

        Assert.Equal([Path.GetFileName(kept.Archive)], Directory.EnumerateFiles(kept.Directory).Select(Path.GetFileName));
        Assert.False(Directory.Exists(other.Directory));
    }

    [Fact]
    public async Task FilesTheGameHoldsOpenAreSkippedAndCounted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to share a file that is open.");
        Write("cfg/server.cfg", "x");
        Write("cfg/locked.cfg", "y");
        var location = Location();

        BackupResult result;
        await using (new FileStream(Path.Combine(_serverDirectory, "cfg", "locked.cfg"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["cfg/server.cfg"], Entries(location.Archive));
        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.SkippedFiles);
    }

    [Fact]
    public async Task NamesNoPathCanNameAreCountedAsSkippedInsteadOfLeftOutSilently()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows names a file with a trailing dot or a device name through \\\\?\\ paths.");
        Write("cfg/server.cfg", "x");
        File.WriteAllText(@"\\?\" + _serverDirectory + @"\cfg\trailing.", "y");
        File.WriteAllText(@"\\?\" + _serverDirectory + @"\cfg\nul.txt", "y");
        var location = Location();

        var result = await _store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);

        Assert.Equal(["cfg/server.cfg"], Entries(location.Archive));
        Assert.Equal((1, 2), (result.FileCount, result.SkippedFiles));
    }

    [Fact]
    public async Task AFileThatGrowsWhileItIsReadLiveIsKeptAtItsListedSize()
    {
        // Read live (no snapshot): a log that grows as fast as it is read must not keep the backup going until the disk fills.
        Write("cfg/growing.cfg", "start");
        var files = new HookedServerFileManager(new DirectServerFileManager())
        {
            OnOpened = (path, content) => path == "cfg/growing.cfg" ? new GrowingStream(content) : content,
        };
        var store = new BackupStore(files, NullLogger<BackupStore>.Instance);
        var location = Location();

        var result = await store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);

        Assert.Equal("start", Read(location.Archive, "cfg/growing.cfg"));
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task TwoFilesThatDifferOnlyByCaseLeaveTheSecondOutSoTheBackupCanBeRestored()
    {
        // A folder with per-directory case sensitivity can hold both; a restore refuses an archive naming the same path twice.
        Write("cfg/Readme.txt", "first");
        var files = new HookedServerFileManager(new DirectServerFileManager())
        {
            OnWalk = tree => [.. tree, new TreeEntry("cfg/readme.txt", FileEntryKind.File, 6, DateTimeOffset.UtcNow)],
        };
        var store = new BackupStore(files, NullLogger<BackupStore>.Instance);
        var location = Location();

        var result = await store.CreateAsync(location, _server, s_rules, Progress, TestContext.Current.CancellationToken);

        Assert.Equal(["cfg/Readme.txt"], Entries(location.Archive));
        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.SkippedFiles);
        await store.RestoreAsync(location, _server, result.Sha256, clean: null, Progress, TestContext.Current.CancellationToken);
        Assert.Equal("first", ReadServer("cfg/Readme.txt"));
    }

    /// <summary>A file the game keeps appending to: after what it held, a megabyte more, as fast as it is read.</summary>
    private sealed class GrowingStream(Stream start) : Stream
    {
        private long _extra = 1024 * 1024;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = start.Read(buffer, offset, count);
            if (read > 0 || _extra == 0)
            {
                return read;
            }

            var more = (int)Math.Min(count, _extra);
            buffer.AsSpan(offset, more).Fill((byte)'g');
            _extra -= more;
            return more;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                start.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private BackupLocation Location() => new(BackupsRoot, "abcd1234", Guid.NewGuid());

    private Task Progress(string stage, double? percent)
    {
        _progress.Add((stage, percent));
        return Task.CompletedTask;
    }

    private void Write(string path, string content)
    {
        var full = Path.Combine(_serverDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string ReadServer(string path) => File.ReadAllText(Path.Combine(_serverDirectory, path.Replace('/', Path.DirectorySeparatorChar)));

    private static List<string> Entries(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return [.. zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    private static string Read(string archive, string name)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
