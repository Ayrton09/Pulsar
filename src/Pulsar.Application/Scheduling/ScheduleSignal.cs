namespace Pulsar.Application.Scheduling;

/// <summary>Wakes the scheduler when tasks change, so it never waits past a new task's first run.</summary>
public sealed class ScheduleSignal : IDisposable
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

    /// <returns>True when tasks changed, false when the time ran out.</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _changed.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _changed.Dispose();
}
