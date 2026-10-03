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

        await _store.RestoreAsync(location, _server, backup.Sha256, Progress, TestContext.Current.CancellationToken);

        Assert.Equal("hostname old", ReadServer("cfg/server.cfg"));
        Assert.Equal("plugin", ReadServer("addons/plugin.smx"));
        Assert.Equal("made later", ReadServer("cfg/new.cfg"));
        Assert.Equal([BackupStages.Checking, BackupStages.Restoring], _progress.Select(p => p.Stage).Distinct());
        Assert.DoesNotContain(Directory.EnumerateFiles(_serverDirectory, "*", SearchOption.AllDirectories),
            f => Path.GetFileName(f).StartsWith(ServerFileRules.TemporaryPrefix, StringComparison.Ordinal));
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
            _store.RestoreAsync(location, _server, backup.Sha256, Progress, TestContext.Current.CancellationToken));

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
            _store.RestoreAsync(location, _server, Hash(location.Archive), Progress, TestContext.Current.CancellationToken));

        Assert.Equal(BackupErrors.Corrupt, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_serverDirectory));
        Assert.False(File.Exists(Path.Combine(_root, "outside.cfg")));
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
            _store.RestoreAsync(location, _server, backup.Sha256, Progress, TestContext.Current.CancellationToken))).Code);
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
