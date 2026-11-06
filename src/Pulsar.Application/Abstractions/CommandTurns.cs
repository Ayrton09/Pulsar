namespace Pulsar.Application.Abstractions;

/// <summary>
/// One command exchange at a time for a resource in this panel process. Unlike a database transaction, the turn can
/// span a committed save and an agent's answer, while the agent's other messages remain free to update the database.
/// </summary>
public sealed class CommandTurns<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Gate> _gates = [];
    private readonly Lock _lock = new();

    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken)
    {
        Gate gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(key, out gate!))
            {
                gate = new Gate();
                _gates.Add(key, gate);
            }

            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, gate);
            throw;
        }

        return new Turn(this, key, gate);
    }

    private void Leave(TKey key, Gate gate)
    {
        lock (_lock)
        {
            if (--gate.Users == 0)
            {
                _gates.Remove(key);
                gate.Dispose();
            }
        }
    }

    private sealed class Gate : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class Turn(CommandTurns<TKey> turns, TKey key, Gate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Semaphore.Release();
                turns.Leave(key, gate);
            }
        }
    }
}
