using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Pulsar.Agent.Core.Servers;

namespace Pulsar.Agent.Core.Tests;

/// <summary>An archive entry is read no further than its header says (the installers' size limits count on it).</summary>
public sealed class BoundedEntryStreamTests
{
    [Fact]
    public async Task AnEntryAsLongAsItsHeaderSaysIsReadWhole()
    {
        using var archive = new ZipArchive(new MemoryStream(Zip("hello world")), ZipArchiveMode.Read);
        await using var content = BoundedEntryStream.Open(archive.Entries.Single());
        using var reader = new StreamReader(content);

        Assert.Equal("hello world", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEntryLongerThanItsHeaderSaysIsRefused()
    {
        var zip = Zip("hello world");
        // The central directory says 2 bytes; the entry holds 11.
        var central = zip.AsSpan().LastIndexOf("PK\u0001\u0002"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24), 2);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        await using var content = BoundedEntryStream.Open(archive.Entries.Single());

        await Assert.ThrowsAsync<InvalidDataException>(() => content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));
    }

    private static byte[] Zip(string text)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("a.txt", CompressionLevel.NoCompression).Open();
            entry.Write(Encoding.UTF8.GetBytes(text));
        }

        return buffer.ToArray();
    }
}
