namespace Pulsar.Application.Scheduling;

/// <summary>Wakes the sequence runner when a sequence starts or moves on, or an automatic update may start.</summary>
public sealed class SequenceSignal : IDisposable
{
    private readonly SemaphoreSlim _changed = new(0, 1);

    public void Notify()
    {
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    /// <returns>True when signalled, false when the time ran out.</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _changed.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _changed.Dispose();
}
