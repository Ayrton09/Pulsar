using System.Text;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The last part of a game's log, to tell whether the game logged an RCON answer itself: positions count every
/// character ever appended, so a position taken before a command still means the same text after more arrived.
/// </summary>
internal sealed class RecentLog
{
    private const int Capacity = 64 * 1024;

    private readonly Lock _lock = new();
    private readonly StringBuilder _text = new();
    private long _start;
    private TaskCompletionSource _appended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Position
    {
        get
        {
            lock (_lock)
            {
                return _start + _text.Length;
            }
        }
    }

    public void Append(string text)
    {
        TaskCompletionSource appended;
        lock (_lock)
        {
            _text.Append(text);
            if (_text.Length > Capacity)
            {
                var dropped = _text.Length - Capacity;
                _text.Remove(0, dropped);
                _start += dropped;
            }

            appended = _appended;
            _appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        appended.TrySetResult();
    }

    /// <summary>Whether <paramref name="text"/> is in the log after <paramref name="since"/>, now or within <paramref name="wait"/>.</summary>
    public async Task<bool> ShowsAsync(string text, long since, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        while (true)
        {
            Task appended;
            lock (_lock)
            {
                var from = (int)Math.Clamp(since - _start, 0, _text.Length);
                if (_text.ToString(from, _text.Length - from).Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }

                appended = _appended.Task;
            }

            try
            {
                await appended.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
