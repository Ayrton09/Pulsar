using System.Diagnostics;
using Pulsar.Application.Databases;

namespace Pulsar.Infrastructure.Databases;

/// <summary>Bounded dump-file writes and the lifetime of the program producing them.</summary>
internal static class GameDatabaseDumpStreams
{
    // A free-space observation cannot be spent by two of our writers at once. Files outside this process can still
    // consume space; this is a floor checked as bytes arrive, not a filesystem quota or a future-space reservation.
    private static readonly SemaphoreSlim s_writes = new(1, 1);

    public static async Task<long> CopyAsync(Stream source, Stream destination, long limit, Action<int> ensureRoom,
        CancellationToken cancellationToken, bool exact = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(ensureRoom);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            // Read at most one byte past the budget, and never write that extra byte.
            var count = (int)Math.Min(buffer.Length, limit - copied);
            if (count < buffer.Length)
            {
                count++;
            }

            var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (read > limit - copied)
            {
                throw new GameDatabaseDumpException($"The dump is larger than {limit} bytes.");
            }

            await s_writes.WaitAsync(cancellationToken);
            try
            {
                ensureRoom(read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            finally
            {
                s_writes.Release();
            }

            copied += read;
        }

        if (exact && copied != limit)
        {
            throw new GameDatabaseDumpException($"The dump ended after {copied} of {limit} bytes.");
        }

        return copied;
    }

    /// <summary>A failed reader/writer must stop the producer before waiting for its pipes to finish.</summary>
    public static async Task WaitAsync(Process process, Task output, Task errors, CancellationTokenSource stopping)
    {
        var exited = process.WaitForExitAsync(stopping.Token);
        List<Task> pending = [exited, output, errors];
        try
        {
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
        }
        catch
        {
            // Observe all started tasks even when the first failure was the writer (the producer could otherwise
            // remain blocked on a full stdout pipe until its hour-long timeout).
            try
            {
                await stopping.CancelAsync();
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It ended while its reader failed.
                }

                await process.WaitForExitAsync(CancellationToken.None);
            }
            finally
            {
                await Task.WhenAll(exited, output, errors).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            throw;
        }
    }
}
