using System.Buffers.Binary;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Api.Sftp;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// The SFTP session's bounded reply queue (docs/architecture/08, T35), over a stream the test controls: a client that does
/// not read holds the session to a few requests, replies leave in the order of their requests, and neither the end of
/// the input nor a cancellation loses a reply or writes one late.
/// </summary>
public sealed class SftpSessionQueueTests
{
    private static readonly TimeSpan s_quiet = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A client that does not read: the INIT, the reply to the INIT held by the stream, the one being sent, two waiting
    /// and the one being handled are all the session takes.
    /// </summary>
    private const int RequestsTakenWithoutReading = 4;

    [Fact]
    public async Task AClientThatDoesNotReadHoldsTheSessionToAFewRequestsAndRepliesComeOutInOrder()
    {
        await using var stream = new DuplexStream();
        using var connection = new SftpConnection(null, null, 0);
        var run = StartAsync(stream, connection, Ct);
        stream.Send(Init());
        for (uint id = 1; id <= 200; id++)
        {
            stream.Send(RealPath(id));
        }

        var expected = Init().Length + (RequestsTakenWithoutReading * RealPath(1).Length);
        await Eventually(() => stream.BytesConsumed >= expected);
        await Task.Delay(s_quiet, Ct);
        Assert.Equal(expected, stream.BytesConsumed);
        Assert.False(run.IsCompleted);

        // The client reads slowly at first: nothing is lost, repeated or reordered.
        var ids = new List<uint>();
        for (var i = 0; i < 201; i++)
        {
            ids.Add(await stream.ReplyIdAsync());
            if (i < 8)
            {
                await Task.Delay(5, Ct);
            }
        }

        Assert.Equal(Enumerable.Range(0, 201).Select(i => (uint)i), ids);
        stream.CompleteInput();
        await run.WaitAsync(SftpApiFactory.Timeout, Ct);
    }

    [Fact]
    public async Task TheEndOfTheInputDeliversTheRepliesStillWaiting()
    {
        await using var stream = new DuplexStream();
        using var connection = new SftpConnection(null, null, 0);
        var run = StartAsync(stream, connection, Ct);
        stream.Send(Init());
        for (uint id = 1; id <= 50; id++)
        {
            stream.Send(RealPath(id));
        }

        stream.CompleteInput();

        // The client has not read anything: the session is still sending.
        await Task.Delay(s_quiet, Ct);
        Assert.False(run.IsCompleted);

        var ids = new List<uint>();
        for (var i = 0; i < 51; i++)
        {
            ids.Add(await stream.ReplyIdAsync());
        }

        Assert.Equal(Enumerable.Range(0, 51).Select(i => (uint)i), ids);
        await run.WaitAsync(SftpApiFactory.Timeout, Ct);
    }

    [Fact]
    public async Task CancellingWhileBlockedEndsTheSessionWithoutLaterWrites()
    {
        await using var stream = new DuplexStream();
        using var connection = new SftpConnection(null, null, 0);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = StartAsync(stream, connection, cancel.Token);
        stream.Send(Init());
        for (uint id = 1; id <= 100; id++)
        {
            stream.Send(RealPath(id));
        }

        var expected = Init().Length + (RequestsTakenWithoutReading * RealPath(1).Length);
        await Eventually(() => stream.BytesConsumed >= expected);
        var written = stream.Writes;

        await cancel.CancelAsync();
        var failure = await Record.ExceptionAsync(() => run.WaitAsync(SftpApiFactory.Timeout, Ct));
        Assert.True(failure is null or OperationCanceledException, $"The session ended with {failure}.");

        // What the stream took before the cancellation is all there will be.
        await Task.Delay(s_quiet, Ct);
        Assert.Equal(written, stream.Writes);
        stream.CompleteOutput();
        var late = 0;
        await foreach (var _ in stream.Replies.ReadAllAsync(Ct))
        {
            late++;
        }

        Assert.Equal(written, late);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task StartAsync(DuplexStream stream, SftpConnection connection, CancellationToken cancellationToken)
    {
        var session = new SftpSession(stream, connection, null!, new SftpOptions(), TimeProvider.System, NullLogger.Instance);
        return Task.Run(() => session.RunAsync(cancellationToken), Ct);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + SftpApiFactory.Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The session did not take the requests in time.");
            await Task.Delay(10, Ct);
        }
    }

    private static byte[] Init()
    {
        using var packet = new SftpWriter(SftpProtocol.Init).UInt32(SftpProtocol.Version);
        return packet.Packet.ToArray();
    }

    private static byte[] RealPath(uint id)
    {
        using var packet = new SftpWriter(SftpProtocol.RealPath).UInt32(id).String("/x/../y");
        return packet.Packet.ToArray();
    }

    /// <summary>
    /// The SSH channel as the session sees it: reads what the test sends, and accepts one write that nobody has read
    /// (the transport's own buffer) before a write waits for the client to read.
    /// </summary>
    private sealed class DuplexStream : Stream
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        private byte[] _current = [];
        private int _position;
        private long _bytesConsumed;
        private long _writes;

        public long BytesConsumed => Interlocked.Read(ref _bytesConsumed);

        public long Writes => Interlocked.Read(ref _writes);

        public ChannelReader<byte[]> Replies => _output.Reader;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Send(byte[] packet) => _input.Writer.TryWrite(packet);

        public void CompleteInput() => _input.Writer.TryComplete();

        public void CompleteOutput() => _output.Writer.TryComplete();

        /// <summary>The request id a reply answers (0 for the answer to the INIT).</summary>
        public async Task<uint> ReplyIdAsync()
        {
            var reply = await _output.Reader.ReadAsync(Ct).AsTask().WaitAsync(SftpApiFactory.Timeout, Ct);
            return reply[4] == SftpProtocol.VersionReply ? 0 : BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(5));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_position >= _current.Length)
            {
                if (!await _input.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                _input.Reader.TryRead(out var next);
                _current = next ?? [];
                _position = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _position);
            _current.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            Interlocked.Add(ref _bytesConsumed, count);
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _output.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
            Interlocked.Increment(ref _writes);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
