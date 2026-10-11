using System.Runtime.CompilerServices;
using System.Text;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Follows the log file a game writes its console to (<c>logfile+rcon</c>, spike 0001): the text appended since the
/// last read, from the start of the file. The agent deletes the file before each start, so the game creates it anew;
/// a file that shrinks (truncated) is read again from its start, and so is a new file under the same name (a game that
/// rotates its log: renames it and starts another), which a reader that is idle checks for every few polls. A byte
/// order mark is skipped. Cancelling ends the enumeration after one last read, so what the game wrote just before
/// exiting is not lost.
/// </summary>
/// <param name="start">Where to start reading: 0, or where an earlier run of the agent stopped (a game left running in its host).</param>
public sealed class LogTail(IServerFiles files, string serverRoot, string relativePath, Encoding encoding, TimeSpan pollInterval, long start = 0)
{
    private const int BufferSize = 64 * 1024;
    private const int IdlePollsPerRotationCheck = 4;
    private const int SignatureLength = 256;
    private long _position = start;

    /// <summary>
    /// How far the file was read and decoded (bytes): where a later reader continues. Bytes of a character whose end was
    /// not written yet are not counted, so that reader does not start in the middle of it.
    /// </summary>
    public long Position => Interlocked.Read(ref _position);

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Stream? stream = null;
        var decoder = encoding.GetDecoder();
        var position = Position;
        var bytes = new byte[BufferSize];
        var chars = new char[encoding.GetMaxCharCount(BufferSize)];
        var tail = new List<byte>(4); // the last bytes read: where a character may be cut
        var lastRead = false;
        var idlePolls = 0;
        try
        {
            while (true)
            {
                stream ??= files.OpenRead(serverRoot, relativePath);
                var restart = stream is not null && stream.Length < position; // truncated
                if (stream is not null && !restart && ++idlePolls % IdlePollsPerRotationCheck == 0 && Replacement(stream, position) is { } replacement)
                {
                    await stream.DisposeAsync();
                    stream = replacement;
                    restart = true;
                }

                if (restart)
                {
                    position = 0;
                    Interlocked.Exchange(ref _position, 0);
                    decoder.Reset();
                    tail.Clear();
                }

                var read = 0;
                if (stream is not null && stream.Length > position)
                {
                    stream.Position = position;
                    // Not cancellable: a local read is quick, and the last one must happen after cancellation.
                    read = await stream.ReadAsync(bytes, CancellationToken.None);
                }

                if (read == 0)
                {
                    if (lastRead)
                    {
                        yield break;
                    }

                    try
                    {
                        await Task.Delay(pollInterval, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        lastRead = true;
                    }

                    continue;
                }

                idlePolls = 0;
                var offset = position == 0 ? Preamble(bytes.AsSpan(0, read)) : 0;
                position += read;
                tail.AddRange(bytes.AsSpan(Math.Max(0, read - 4), Math.Min(read, 4)));
                tail.RemoveRange(0, Math.Max(0, tail.Count - 4));
                Interlocked.Exchange(ref _position, position - Incomplete(tail));
                var count = decoder.GetChars(bytes, offset, read - offset, chars, 0);
                if (count > 0)
                {
                    yield return new string(chars, 0, count);
                }
            }
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The file the path names now, when it is not the one being read (<paramref name="current"/>, read up to
    /// <paramref name="position"/>): it is longer than that one (a file being written only grows, and its own length is
    /// read last), shorter than what was read, or starts differently.
    /// </summary>
    private Stream? Replacement(Stream current, long position)
    {
        if (current.Length > position)
        {
            return null; // what it still holds is read first
        }

        Stream? named;
        try
        {
            named = files.OpenRead(serverRoot, relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // being replaced right now: checked again later
        }

        if (named is null)
        {
            return null; // renamed, and the game has not created the next one yet
        }

        var namedLength = named.Length;
        if (namedLength > current.Length || namedLength < position || !SameStart(current, named, (int)Math.Min(position, SignatureLength)))
        {
            return named;
        }

        named.Dispose();
        return null;
    }

    private static bool SameStart(Stream first, Stream second, int length)
    {
        if (length == 0)
        {
            return true;
        }

        Span<byte> a = stackalloc byte[length];
        Span<byte> b = stackalloc byte[length];
        first.Position = 0;
        second.Position = 0;
        first.ReadAtLeast(a, length, throwOnEndOfStream: false);
        second.ReadAtLeast(b, length, throwOnEndOfStream: false);
        return a.SequenceEqual(b);
    }

    /// <summary>
    /// How many of the last bytes read (<paramref name="tail"/>, up to four) start a UTF-8 character whose end is not
    /// there yet: the decoder holds them back until it comes. Single-byte code pages never split a character; a split
    /// double-byte character (OEM and ANSI code pages of East Asian locales) is not detected, and a reader that continues
    /// there shows one replacement character.
    /// </summary>
    private int Incomplete(List<byte> tail)
    {
        if (encoding.CodePage != Encoding.UTF8.CodePage)
        {
            return 0;
        }

        for (var back = 1; back <= Math.Min(3, tail.Count); back++)
        {
            var current = tail[^back];
            if ((current & 0xC0) == 0x80)
            {
                continue; // a continuation byte: its lead byte is further back
            }

            var length = current >= 0xF0 ? 4 : current >= 0xE0 ? 3 : current >= 0xC0 ? 2 : 1;
            return length > back ? back : 0;
        }

        return 0;
    }

    private int Preamble(ReadOnlySpan<byte> start)
    {
        var preamble = encoding.Preamble;
        return preamble.Length > 0 && start.StartsWith(preamble) ? preamble.Length : 0;
    }
}
