using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Tests;

/// <summary>Plain file access that records which files were made executable (Linux' execute bits).</summary>
internal sealed class ExecutableFiles : IServerFiles
{
    private readonly DirectServerFiles _inner = new();

    public List<string> Executables { get; } = [];

    public Stream? OpenRead(string serverRoot, string relativePath) => _inner.OpenRead(serverRoot, relativePath);

    public void Write(string serverRoot, string relativePath, Stream content) => _inner.Write(serverRoot, relativePath, content);

    public void Delete(string serverRoot, string relativePath) => _inner.Delete(serverRoot, relativePath);

    public void CreateDirectory(string serverRoot, string relativePath) => _inner.CreateDirectory(serverRoot, relativePath);

    public bool DeleteEmptyDirectory(string serverRoot, string relativePath) => _inner.DeleteEmptyDirectory(serverRoot, relativePath);

    public IReadOnlyList<(string Name, DateTimeOffset LastWrite)> ListFiles(string serverRoot, string relativeDirectory, int limit) =>
        _inner.ListFiles(serverRoot, relativeDirectory, limit);

    public void SetExecutable(string serverRoot, string relativePath) => Executables.Add(relativePath);
}

/// <summary>Tars as Linux builds ship them: files with their modes (0755 for programs).</summary>
internal static class TestTars
{
    public const UnixFileMode Program = (UnixFileMode)0x1ED; // 0755

    public const UnixFileMode Plain = (UnixFileMode)0x1A4; // 0644

    public static PaxTarEntry File(string name, string content, UnixFileMode mode = Plain) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = mode };

    public static PaxTarEntry Folder(string name) => new(TarEntryType.Directory, name) { Mode = Program };

    public static byte[] Tar(params TarEntry[] entries)
    {
        using var buffer = new MemoryStream();
        using (var tar = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                tar.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] TarGz(params TarEntry[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Tar(entries));
        }

        return buffer.ToArray();
    }

    /// <summary>A zip whose files are <paramref name="files"/>, as bytes.</summary>
    public static byte[] Zip(params (string Path, byte[] Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }
}
