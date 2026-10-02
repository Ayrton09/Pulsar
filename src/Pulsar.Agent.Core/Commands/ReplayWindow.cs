namespace Pulsar.Agent.Core.Commands;

/// <summary>
/// Sliding anti-replay window over command sequence numbers (as in IPsec): every number is accepted at most once,
/// slightly reordered numbers inside the window are still accepted, and anything older than the window is refused.
/// </summary>
public sealed class ReplayWindow
{
    public const int DefaultSize = 1024;

    private readonly ulong[] _seen;
    private readonly int _size;
    private readonly Lock _lock = new();
    private long _highest;

    public ReplayWindow(int size = DefaultSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 64);
        if (size % 64 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The window size must be a multiple of 64.");
        }

        _size = size;
        _seen = new ulong[size / 64];
    }

    /// <summary>Records <paramref name="sequence"/> and returns true the first time it is seen inside the window.</summary>
    public bool TryAccept(long sequence)
    {
        if (sequence <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            if (sequence > _highest)
            {
                // Forget the slots the window slides past: they belong to numbers that are now too old.
                var advance = sequence - _highest;
                if (advance >= _size)
                {
                    Array.Clear(_seen);
                }
                else
                {
                    for (var s = _highest + 1; s <= sequence; s++)
                    {
                        Clear(s);
                    }
                }

                _highest = sequence;
                Set(sequence);
                return true;
            }

            if (_highest - sequence >= _size || IsSet(sequence))
            {
                return false;
            }

            Set(sequence);
            return true;
        }
    }

    private int Slot(long sequence) => (int)(sequence % _size);

    private bool IsSet(long sequence) => (_seen[Slot(sequence) / 64] & (1UL << (Slot(sequence) % 64))) != 0;

    private void Set(long sequence) => _seen[Slot(sequence) / 64] |= 1UL << (Slot(sequence) % 64);

    private void Clear(long sequence) => _seen[Slot(sequence) / 64] &= ~(1UL << (Slot(sequence) % 64));
}
