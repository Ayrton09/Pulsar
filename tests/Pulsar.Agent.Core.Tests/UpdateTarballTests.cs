using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pulsar.Agent.Core.Updates;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The Linux tarball of an update: its one folder's files and folders, and nothing else.</summary>
public sealed class UpdateTarballTests : IDisposable
{
    private const string Top = "pulsar-1.0.1-linux-x64";
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-tarball-tests-").FullName;

    [Fact]
    public void ItsFolderIsUnpackedWithProgramsExecutableAndNothingElseKept()
    {
        var archive = Save(Tarball(Top, ("release.json", "{}"), ("agent/PulsarAgent", "program"), ("agent/lib.so.txt", "plain")));
        var destination = Path.Combine(_root, "release");

        UpdateTarball.Extract(archive, destination, Top);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(destination, "release.json")));
        Assert.Equal("program", File.ReadAllText(Path.Combine(destination, "agent", "PulsarAgent")));
        Assert.False(Directory.Exists(Path.Combine(destination, Top)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(destination, "agent", "PulsarAgent")));
            Assert.Equal((UnixFileMode)0x1A4, File.GetUnixFileMode(Path.Combine(destination, "agent", "lib.so.txt")));
            Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(destination, "agent")));
        }
    }

    [Theory]
    [InlineData("pulsar-1.0.2-linux-x64/release.json")]
    [InlineData("release.json")]
    [InlineData(Top + "/../evil")]
    [InlineData(Top + "/agent/../../evil")]
    [InlineData(Top + "//evil")]
    [InlineData("/" + Top + "/evil")]
    public void AnEntryOutsideItsFolderRefusesIt(string name)
    {
        var archive = Save(Write(tar => tar.WriteEntry(Entry(name, "x"))));

        Assert.Throws<InvalidDataException>(() => UpdateTarball.Extract(archive, Path.Combine(_root, "release"), Top));
        Assert.False(File.Exists(Path.Combine(_root, "evil")));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Fifo)]
    [InlineData(TarEntryType.CharacterDevice)]
    public void ALinkOrDeviceRefusesIt(TarEntryType type)
    {
        var archive = Save(Write(tar =>
        {
            var entry = new PaxTarEntry(type, Top + "/agent/PulsarAgent");
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                entry.LinkName = "/etc/shadow";
            }

            tar.WriteEntry(entry);
        }));

        var refused = Assert.Throws<InvalidDataException>(() => UpdateTarball.Extract(archive, Path.Combine(_root, "release"), Top));
        Assert.Contains("only folders and files are taken", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWrittenTwiceRefusesIt()
    {
        var archive = Save(Write(tar =>
        {
            tar.WriteEntry(Entry(Top + "/release.json", "{}"));
            tar.WriteEntry(Entry(Top + "/release.json", "{\"version\":\"9.9.9\"}"));
        }));

        Assert.Throws<IOException>(() => UpdateTarball.Extract(archive, Path.Combine(_root, "release"), Top));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A release's tarball as the release packer makes it: its folder, then its files (in folders of their own).</summary>
    internal static byte[] Tarball(string top, params (string Name, string Content)[] files) => Write(tar =>
    {
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = (UnixFileMode)0x1ED });
        foreach (var (name, content) in files)
        {
            var entry = Entry(top + "/" + name, content);
            entry.Mode = name.EndsWith("PulsarAgent", StringComparison.Ordinal) || name.EndsWith(".sh", StringComparison.Ordinal)
                ? (UnixFileMode)0x1ED : (UnixFileMode)0x1A4;
            tar.WriteEntry(entry);
        }
    });

    private static PaxTarEntry Entry(string name, string content) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = (UnixFileMode)0x1A4 };

    private static byte[] Write(Action<TarWriter> entries)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            entries(tar);
        }

        return stream.ToArray();
    }

    private string Save(byte[] tarball)
    {
        var path = Path.Combine(_root, "release.tar.gz");
        File.WriteAllBytes(path, tarball);
        return path;
    }
}
