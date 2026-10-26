using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// The file manager's zips (Pulsar 1.0.11; docs/architecture/08, "File manager"): what goes into a zip, and what a zip
/// may write. Every refusal comes before a single file is written.
/// </summary>
public sealed class FileArchivesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-archive-tests", Guid.NewGuid().ToString("N"));
    private readonly ProvisionedServer _server;
    private readonly FileArchives _archives = new(new DirectServerFileManager(), NullLogger<FileArchives>.Instance);
    private readonly List<(string Stage, double? Percent)> _progress = [];

    public FileArchivesTests()
    {
        _server = new ProvisionedServer(Directory.CreateDirectory(Path.Combine(_root, "server")).FullName, ProcessIdentity.CurrentUser);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AFolderIsZippedUnderItsNameAndWrittenOutAgainAsItWas()
    {
        Write("world/level.dat", "level");
        Write("world/region/r.0.0.mca", new string('r', 300_000));
        Directory.CreateDirectory(Path.Combine(_server.Directory, "world", "empty"));
        Write("world/.pulsar-upload-0123456789abcdef.tmp", "half an upload");
        Write("server.properties", "motd=hi");
        Write("other/ignored.txt", "not selected");

        var made = await _archives.CompressAsync(_server, "", ["world", "server.properties"], "world.zip", Progress, Ct);

        Assert.Equal(["server.properties", "world/", "world/empty/", "world/level.dat", "world/region/", "world/region/r.0.0.mca"],
            Names("world.zip"));
        Assert.Equal((3, 0, new FileInfo(Full("world.zip")).Length), (made.Files, made.Skipped, made.Size));
        Assert.Contains(_progress, p => p.Stage == ArchiveStages.Archiving && p.Percent == 100);

        var written = await _archives.ExtractAsync(_server, "world.zip", "copy", overwrite: false, Progress, Ct);

        Assert.Equal(3, written.Files);
        Assert.Equal(new string('r', 300_000), Read("copy/world/region/r.0.0.mca"));
        Assert.Equal("motd=hi", Read("copy/server.properties"));
        Assert.True(Directory.Exists(Full("copy/world/empty")));
    }

    [Fact]
    public async Task AZipInsideAFolderNamesItsEntriesFromThatFolder()
    {
        Write("plugins/Essentials/config.yml", "a: 1");

        await _archives.CompressAsync(_server, "plugins", ["Essentials"], "plugins/Essentials.zip", Progress, Ct);

        Assert.Equal(["Essentials/", "Essentials/config.yml"], Names("plugins/Essentials.zip"));
    }

    [Theory]
    [InlineData("", "missing", "x.zip", FileErrors.NotFound)]
    [InlineData("", ".pulsar", "x.zip", FileErrors.InvalidPath)]
    [InlineData("", "world", ".pulsar/x.zip", FileErrors.AccessDenied)]
    [InlineData(".pulsar", "steamcmd", "x.zip", FileErrors.AccessDenied)]
    [InlineData("", "world/level.dat", "x.zip", FileErrors.InvalidPath)]
    [InlineData("", "..", "x.zip", FileErrors.InvalidPath)]
    [InlineData("", "world", "", FileErrors.InvalidPath)]
    public async Task OnlyNamesOfTheFolderOutsidePulsarsOwnAreZipped(string directory, string name, string archive, string code)
    {
        Write("world/level.dat", "level");
        Write(".pulsar/steamcmd/config/config.vdf", "a Steam login");

        var refused = await Assert.ThrowsAsync<FileManagerException>(() =>
            _archives.CompressAsync(_server, directory, [name], archive, Progress, Ct));

        Assert.True(refused.Code == code || (code == FileErrors.InvalidPath && refused.Code == FileErrors.AccessDenied), $"{refused.Code}: {refused.Message}");
        Assert.False(File.Exists(Full("x.zip")));
    }

    [Fact]
    public async Task WhatNoPathCanNameUnderTheSelectionIsCountedAsLeftOut()
    {
        // A device name a game made through extended paths: the walk cannot name it, so the zip cannot hold it.
        Write("world/level.dat", "level");
        Write("other/keep.txt", "not selected");
        File.WriteAllText(Extended(Full("world/nul.txt")), "unnameable");
        File.WriteAllText(Extended(Full("other/nul.txt")), "unnameable, and not selected");

        var made = await _archives.CompressAsync(_server, "", ["world"], "world.zip", Progress, Ct);

        Assert.Equal((1, 1), (made.Files, made.Skipped));
        Assert.Equal(["world/", "world/level.dat"], Names("world.zip"));
        File.Delete(Extended(Full("world/nul.txt")));
        File.Delete(Extended(Full("other/nul.txt")));
    }

    [Fact]
    public async Task AZipWhoseListOfEntriesIsTooLargeIsRefusedBeforeItIsRead()
    {
        // A small zip whose end record says its list starts at the beginning of a file far larger than any list should be.
        WriteZip("small.zip", ("a.txt", "a"));
        var zip = File.ReadAllBytes(Full("small.zip"));
        var end = zip.Length - 22;
        BitConverter.TryWriteBytes(zip.AsSpan(end + 16, 4), 0u);
        using (var padded = File.Create(Full("padded.zip")))
        {
            padded.SetLength(FileArchives.MaxDirectoryBytes + 1);
            padded.Position = padded.Length;
            padded.Write(zip);
        }

        await using (var stream = File.OpenRead(Full("padded.zip")))
        {
            var early = await Assert.ThrowsAsync<FileManagerException>(() => FileArchives.CheckDirectoryAsync(stream, Ct));
            Assert.Contains("too large to read", early.Message, StringComparison.Ordinal);
        }

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "padded.zip", "out", overwrite: true, Progress, Ct));

        Assert.Equal(FileErrors.ArchiveInvalid, refused.Code);
        Assert.False(Directory.Exists(Full("out")));
    }

    [Fact]
    public async Task AZip64EndRecordClaimingTooManyEntriesIsRefusedBeforeItIsRead()
    {
        // Its Zip64 end record, the locator pointing at it, and the classic end record whose fields say "see Zip64".
        var tail = new byte[56 + 20 + 22];
        BitConverter.TryWriteBytes(tail.AsSpan(0), 0x06064b50u);
        BitConverter.TryWriteBytes(tail.AsSpan(32), (long)FileArchives.MaxEntries + 1);
        BitConverter.TryWriteBytes(tail.AsSpan(56), 0x07064b50u);
        BitConverter.TryWriteBytes(tail.AsSpan(56 + 8), 0L);
        BitConverter.TryWriteBytes(tail.AsSpan(76), 0x06054b50u);
        BitConverter.TryWriteBytes(tail.AsSpan(76 + 10), ushort.MaxValue);
        BitConverter.TryWriteBytes(tail.AsSpan(76 + 16), uint.MaxValue);
        using var stream = new MemoryStream(tail);

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => FileArchives.CheckDirectoryAsync(stream, Ct));

        Assert.Equal(FileErrors.ArchiveInvalid, refused.Code);
        Assert.Contains("more than", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryZipPassesTheEndRecordCheckAndIsReadFromTheStart()
    {
        WriteZip("plain.zip", ("a.txt", "a"), ("b/c.txt", "c"));
        await using var stream = File.OpenRead(Full("plain.zip"));

        await FileArchives.CheckDirectoryAsync(stream, Ct);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task AZipMadeInsideTheFolderItZipsDoesNotHoldItself()
    {
        Write("world/level.dat", "level");

        await _archives.CompressAsync(_server, "", ["world"], "world/world.zip", Progress, Ct);

        Assert.Equal(["world/", "world/level.dat"], Names("world/world.zip"));
    }

    [Fact]
    public async Task AFileTheGameLocksIsLeftOutAndCounted()
    {
        // Minecraft keeps a lock on world/session.lock: opening it works, reading it does not (Windows' byte-range locks).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Byte-range locks keep other handles from reading only on Windows.");
        Write("world/level.dat", "level");
        Write("world/session.lock", "locked");
        using var game = new FileStream(Full("world/session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (OperatingSystem.IsWindows())
        {
            game.Lock(0, game.Length);
        }

        var made = await _archives.CompressAsync(_server, "", ["world"], "world.zip", Progress, Ct);

        Assert.Equal((1, 1), (made.Files, made.Skipped));
        Assert.Equal(["world/", "world/level.dat"], Names("world.zip"));
    }

    [Fact]
    public async Task AnExistingZipIsNeverReplaced()
    {
        Write("world/level.dat", "level");
        Write("world.zip", "mine");

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.CompressAsync(_server, "", ["world"], "world.zip", Progress, Ct));

        Assert.Equal(FileErrors.Exists, refused.Code);
        Assert.Equal("mine", Read("world.zip"));
        Assert.Empty(Directory.GetFiles(_server.Directory, ".pulsar-upload-*"));
    }

    [Theory]
    // Out of the folder, or Pulsar's own, by any spelling a zip can hold.
    [InlineData("../escaped.txt")]
    [InlineData("a/../../escaped.txt")]
    [InlineData("/etc/escaped.txt")]
    [InlineData("C:/escaped.txt")]
    [InlineData("..\\escaped.txt")]
    [InlineData("a\\..\\..\\escaped.txt")]
    [InlineData(".pulsar/steamcmd/config/config.vdf")]
    [InlineData(".PULSAR/x")]
    [InlineData("PULSAR~1/x")]
    [InlineData("a/./b.txt")]
    [InlineData("CON")]
    [InlineData("a/nul.txt")]
    [InlineData("a/b.txt:stream")]
    [InlineData("trailing. ")]
    public async Task AnEntryThatIsNotAPlainPathRefusesTheWholeZip(string name)
    {
        WriteZip("bad.zip", ("ok.txt", "fine"), (name, "bad"));

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "bad.zip", "", overwrite: true, Progress, Ct));

        Assert.Equal(FileErrors.ArchiveInvalid, refused.Code);
        Assert.False(File.Exists(Full("ok.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public async Task ADestinationInPulsarsFolderIsRefused()
    {
        WriteZip("mods.zip", ("a.jar", "a"));

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "mods.zip", ".pulsar/steamcmd", overwrite: true, Progress, Ct));

        Assert.Equal(FileErrors.AccessDenied, refused.Code);
    }

    [Theory]
    [InlineData("A.txt", "a.TXT")]
    [InlineData("dir/x", "DIR/x")]
    [InlineData("a", "a/b")]
    public async Task APathTheZipNamesTwiceByAnyCaseOrAsFileAndFolderRefusesIt(string first, string second)
    {
        WriteZip("twice.zip", (first, "1"), (second, "2"));

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "twice.zip", "out", overwrite: true, Progress, Ct));

        Assert.Equal(FileErrors.ArchiveInvalid, refused.Code);
        Assert.False(Directory.Exists(Full("out")));
    }

    [Fact]
    public async Task WindowsSeparatorsAreReadAsFolders()
    {
        // As PowerShell 5's Compress-Archive writes them.
        WriteZip("ps5.zip", ("world\\region\\r.0.0.mca", "region"), ("world\\level.dat", "level"));

        await _archives.ExtractAsync(_server, "ps5.zip", "", overwrite: false, Progress, Ct);

        Assert.Equal("region", Read("world/region/r.0.0.mca"));
    }

    [Fact]
    public async Task WithoutOverwriteAnExistingFileRefusesTheZipBeforeAnythingIsWritten()
    {
        Write("out/b.txt", "mine");
        WriteZip("files.zip", ("a.txt", "new a"), ("b.txt", "new b"));

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "files.zip", "out", overwrite: false, Progress, Ct));

        Assert.Equal(FileErrors.Exists, refused.Code);
        Assert.False(File.Exists(Full("out/a.txt")));
        Assert.Equal("mine", Read("out/b.txt"));

        await _archives.ExtractAsync(_server, "files.zip", "out", overwrite: true, Progress, Ct);

        Assert.Equal(("new a", "new b"), (Read("out/a.txt"), Read("out/b.txt")));
    }

    [Fact]
    public async Task AnEntryLongerThanItSaysIsDamagedAndItsFileNeverAppears()
    {
        // A zip bomb's trick: a small declared size, much more when inflated.
        var zip = Full("bomb.zip");
        using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("bomb.bin", CompressionLevel.Optimal).Open();
            entry.Write(new byte[100_000]);
        }

        SetDeclaredSize(zip, "bomb.bin", 10);

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "bomb.zip", "out", overwrite: true, Progress, Ct));

        Assert.Equal(FileErrors.ArchiveInvalid, refused.Code);
        Assert.False(File.Exists(Full("out/bomb.bin")));
    }

    [Fact]
    public async Task WhatDoesNotFitOnTheDriveIsRefusedFirst()
    {
        // Entries that say they hold nearly 4 GiB each, more of them than the drive has room for.
        var zip = Full("huge.zip");
        var count = (int)(new DriveInfo(Path.GetPathRoot(_server.Directory)!).AvailableFreeSpace / (uint.MaxValue - 1)) + 2;
        using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            for (var i = 0; i < count; i++)
            {
                using var entry = archive.CreateEntry($"huge{i}.bin", CompressionLevel.Optimal).Open();
                entry.Write(new byte[1000]);
            }
        }

        for (var i = 0; i < count; i++)
        {
            SetDeclaredSize(zip, $"huge{i}.bin", uint.MaxValue - 1);
        }

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "huge.zip", "out", overwrite: true, Progress, Ct));

        Assert.True(refused.Code is FileErrors.DiskFull or FileErrors.ArchiveInvalid, refused.Code);
        Assert.False(Directory.Exists(Full("out")));
    }

    [Fact]
    public async Task AnEntryWhoseBytesChangedIsDamaged()
    {
        using (var stream = File.Create(Full("changed.zip")))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("a.txt", CompressionLevel.NoCompression).Open(), Encoding.ASCII))
        {
            writer.Write("the original text");
        }

        var bytes = File.ReadAllBytes(Full("changed.zip"));
        var at = Encoding.ASCII.GetString(bytes).IndexOf("original", StringComparison.Ordinal);
        Assert.True(at > 0, "stored, not compressed");
        bytes[at] = (byte)'X';
        File.WriteAllBytes(Full("changed.zip"), bytes);

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "changed.zip", "out", overwrite: true, Progress, Ct));

        Assert.Equal(FileErrors.ArchiveInvalid, refused.Code);
        Assert.False(File.Exists(Full("out/a.txt")));
    }

    [Fact]
    public async Task SomethingThatIsNotAZipSaysSo()
    {
        Write("notes.zip", "not a zip at all");

        var refused = await Assert.ThrowsAsync<FileManagerException>(() => _archives.ExtractAsync(_server, "notes.zip", "out", overwrite: true, Progress, Ct));

        Assert.Equal(FileErrors.ArchiveInvalid, refused.Code);
    }

    [Fact]
    public async Task ACancelledZipLeavesNothing()
    {
        Write("world/region/r.0.0.mca", new string('r', 100_000));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _archives.CompressAsync(_server, "", ["world"], "world.zip", Progress, cancelled.Token));

        Assert.False(File.Exists(Full("world.zip")));
        Assert.Empty(Directory.GetFiles(_server.Directory, ".pulsar-upload-*"));
    }

    private Task Progress(string stage, double? percent)
    {
        _progress.Add((stage, percent));
        return Task.CompletedTask;
    }

    private string Full(string path) => Path.Combine(_server.Directory, path.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string path, string content)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Read(string path) => File.ReadAllText(Full(path));

    /// <summary>On Windows, a path that reaches names Win32 would change (device names, trailing dots).</summary>
    private static string Extended(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;

    private List<string> Names(string zip)
    {
        using var archive = ZipFile.OpenRead(Full(zip));
        return [.. archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    private void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(Full(path));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    /// <summary>Rewrites the uncompressed size the zip records for <paramref name="name"/> (local header and central directory), Zip64 when it must.</summary>
    private static void SetDeclaredSize(string zip, string name, long size)
    {
        var bytes = File.ReadAllBytes(zip);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            var local = BitConverter.ToUInt32(bytes, i) == 0x04034b50 && BitConverter.ToUInt16(bytes, i + 26) == nameBytes.Length
                && bytes.AsSpan(i + 30, nameBytes.Length).SequenceEqual(nameBytes);
            var central = BitConverter.ToUInt32(bytes, i) == 0x02014b50 && BitConverter.ToUInt16(bytes, i + 28) == nameBytes.Length
                && bytes.AsSpan(i + 46, nameBytes.Length).SequenceEqual(nameBytes);
            if (local || central)
            {
                var at = local ? i + 22 : i + 24;
                BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), (uint)Math.Min(size, uint.MaxValue - 1));
            }
        }

        File.WriteAllBytes(zip, bytes);
    }
}
