namespace Pulsar.Agent.Core.Backups;

/// <summary>
/// The stream a backup's archive is written to: every <paramref name="interval"/> bytes written it calls
/// <paramref name="check"/> with that many bytes, which throws when the drive could not take the next ones without going
/// below the node's disk floor. Whatever else fills the drive meanwhile (the game, its logs, PostgreSQL) then stops the
/// backup, instead of the backup writing until nothing is left.
/// </summary>
internal sealed class SpaceGuardStream(Stream inner, long interval, Action<long> check) : Stream
{
    private long _sinceCheck;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Guard(buffer.Length);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Guard(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private void Guard(int written)
    {
        _sinceCheck += written;
        if (_sinceCheck >= interval)
        {
            _sinceCheck = 0;
            check(interval);
        }
    }
}
