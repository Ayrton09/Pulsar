using System.IO.Compression;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// An archive entry's content, read no further than the size its header declares: the archive's limits are checked
/// against those sizes, and an entry that decompresses to more (a lying header) fails instead of filling the disk.
/// </summary>
public sealed class BoundedEntryStream : Stream
{
    private readonly Stream _inner;
    private long _left;

    private BoundedEntryStream(Stream inner, long length)
    {
        _inner = inner;
        _left = length;
    }

    public static Stream Open(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new BoundedEntryStream(entry.Open(), entry.Length);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        // One byte more than is left is asked for: getting it means the entry is longer than declared.
        var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _left + 1)]);
        return Counted(read);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left + 1)], cancellationToken);
        return Counted(read);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private int Counted(int read)
    {
        _left -= read;
        return _left < 0 ? throw new InvalidDataException("An archive entry is larger than its header says.") : read;
    }
}
