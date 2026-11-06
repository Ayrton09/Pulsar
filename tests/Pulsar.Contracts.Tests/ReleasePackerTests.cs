using System.Formats.Tar;
using System.IO.Compression;
using Pulsar.ReleasePacker;

namespace Pulsar.Contracts.Tests;

public sealed class ReleasePackerTests : IDisposable
{
    private static readonly DateTimeOffset s_commitTime = DateTimeOffset.FromUnixTimeSeconds(1_793_000_000);

    private readonly string _scratch = Directory.CreateTempSubdirectory("pulsar-packer-").FullName;

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Fact]
    public void TheTarballIsRootsWithProgramsExecutable()
    {
        var release = Release();

        var entries = Read(Pack(release, "release.tar.gz"));

        Assert.Equal(
            [
                "pulsar-1.0.0-linux-x64/",
                "pulsar-1.0.0-linux-x64/install-pulsar.sh",
                "pulsar-1.0.0-linux-x64/panel/",
                "pulsar-1.0.0-linux-x64/panel/Pulsar.Api",
                "pulsar-1.0.0-linux-x64/panel/appsettings.json",
                "pulsar-1.0.0-linux-x64/panel/wwwroot/",
                "pulsar-1.0.0-linux-x64/panel/wwwroot/index.html",
                "pulsar-1.0.0-linux-x64/release.json",
            ],
            entries.Select(e => e.Name));
        Assert.All(entries, e =>
        {
            Assert.Equal(0, e.Uid);
            Assert.Equal(0, e.Gid);
            Assert.Equal(s_commitTime, e.ModificationTime);
        });
        Assert.All(entries.Where(e => e.EntryType == TarEntryType.Directory), e => Assert.Equal(TarPacker.DirectoryMode, e.Mode));
        Assert.Equal(TarPacker.ProgramMode, entries.Single(e => e.Name.EndsWith("/Pulsar.Api", StringComparison.Ordinal)).Mode);
        Assert.Equal(TarPacker.ProgramMode, entries.Single(e => e.Name.EndsWith(".sh", StringComparison.Ordinal)).Mode);
        Assert.Equal(TarPacker.FileMode, entries.Single(e => e.Name.EndsWith("release.json", StringComparison.Ordinal)).Mode);
        Assert.Equal(TarPacker.FileMode, entries.Single(e => e.Name.EndsWith("index.html", StringComparison.Ordinal)).Mode);
    }

    [Fact]
    public void TheSameTreePacksTheSameBytes()
    {
        var release = Release();

        var first = Pack(release, "first.tar.gz");
        Directory.SetLastWriteTimeUtc(release, DateTime.UtcNow.AddDays(-3));
        var second = Pack(release, "second.tar.gz");

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public void AnOutputThereAlreadyIsNotReplaced()
    {
        var release = Release();
        var output = Path.Combine(_scratch, "taken.tar.gz");
        File.WriteAllText(output, "someone else's");

        Assert.Throws<IOException>(() => TarPacker.Pack(release, output, s_commitTime));
        Assert.Equal("someone else's", File.ReadAllText(output));
    }

    [Fact]
    public void ALinkIsRefused()
    {
        var release = Release();
        try
        {
            File.CreateSymbolicLink(Path.Combine(release, "panel", "elsewhere"), Path.Combine(_scratch, "outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account cannot make symbolic links here.");
        }

        Assert.Throws<InvalidOperationException>(() => TarPacker.Pack(release, Path.Combine(_scratch, "linked.tar.gz"), s_commitTime));
    }

    private string Release()
    {
        var root = Directory.CreateDirectory(Path.Combine(_scratch, "pulsar-1.0.0-linux-x64")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "panel", "wwwroot"));
        File.WriteAllBytes(Path.Combine(root, "panel", "Pulsar.Api"), [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]);
        File.WriteAllText(Path.Combine(root, "panel", "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(root, "panel", "wwwroot", "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(root, "install-pulsar.sh"), "#!/usr/bin/env bash\n");
        File.WriteAllText(Path.Combine(root, "release.json"), """{"version":"1.0.0"}""");
        return root;
    }

    private string Pack(string release, string name)
    {
        var output = Path.Combine(_scratch, name);
        TarPacker.Pack(release, output, s_commitTime);
        return output;
    }

    private static List<TarEntry> Read(string tarball)
    {
        using var file = File.OpenRead(tarball);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new List<TarEntry>();
        while (reader.GetNextEntry() is { } entry)
        {
            entries.Add(entry);
        }

        return entries;
    }
}
