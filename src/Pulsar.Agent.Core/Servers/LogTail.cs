using System.Runtime.CompilerServices;
using System.Text;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Follows the log file a game writes its console to (<c>logfile+rcon</c>, spike 0001): the text appended since the
/// last read, from the start of the file. The agent deletes the file before each start, so the game creates it anew;
/// a file that shrinks (truncated) is read again from its start. A byte order mark is skipped.
/// </summary>
public sealed class LogTail(IServerFiles files, string serverRoot, string relativePath, Encoding encoding, TimeSpan pollInterval)
{
    private const int BufferSize = 64 * 1024;

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Stream? stream = null;
        var decoder = encoding.GetDecoder();
        var position = 0L;
        var bytes = new byte[BufferSize];
        var chars = new char[encoding.GetMaxCharCount(BufferSize)];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                stream ??= files.OpenRead(serverRoot, relativePath);
                if (stream is not null && stream.Length < position)
                {
                    position = 0;
                    decoder.Reset();
                }

                var read = 0;
                if (stream is not null && stream.Length > position)
                {
                    stream.Position = position;
                    read = await stream.ReadAsync(bytes, cancellationToken);
                }

                if (read == 0)
                {
                    try
                    {
                        await Task.Delay(pollInterval, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        yield break;
                    }

                    continue;
                }

                var offset = position == 0 ? Preamble(bytes.AsSpan(0, read)) : 0;
                position += read;
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

    private int Preamble(ReadOnlySpan<byte> start)
    {
        var preamble = encoding.Preamble;
        return preamble.Length > 0 && start.StartsWith(preamble) ? preamble.Length : 0;
    }
}
