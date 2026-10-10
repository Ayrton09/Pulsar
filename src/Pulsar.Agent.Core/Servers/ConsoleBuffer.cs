using System.Text;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The recent console output of one server, in memory (docs/architecture/03, "Console: flow and volume"): what the panel
/// gets when a browser starts watching, or after a reconnect. Bounded: the oldest output leaves first.
/// <para>
/// Output arrives in small pieces; consecutive pieces join the last entry while it is younger than
/// <see cref="MergeWindow"/>, shorter than <see cref="MaxEntryLength"/> and nobody has read it yet. Once read, an entry
/// never changes (later output goes to a new entry), so a sequence always means the same text.
/// </para>
/// <para>
/// Sequences start at the creation time in microseconds since 1970: after an agent restart they continue above
/// everything the previous run used, so the panel can tell new output from output it already has.
/// </para>
/// </summary>
public sealed class ConsoleBuffer
{
    /// <summary>About one million characters (2 MiB in memory).</summary>
    public const int DefaultCapacity = 1024 * 1024;

    public const int MaxEntryLength = 16 * 1024;

    public static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(50);

    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];
    private readonly int _capacity;
    private int _start; // entries before it were evicted (removed from the list in batches)
    private long _next;
    private int _length;
    private TaskCompletionSource _appended = NewSignal();

    public ConsoleBuffer(TimeProvider clock, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, MaxEntryLength);
        _capacity = capacity;
        _next = clock.GetUtcNow().ToUnixTimeMilliseconds() * 1000;
    }

    /// <summary>The sequence of the newest entry (one less than the first sequence when nothing was written yet).</summary>
    public long LastSequence
    {
        get
        {
            lock (_lock)
            {
                return _next - 1;
            }
        }
    }

    /// <summary>
    /// The sequence of the newest entry, which no longer changes from now on: everything written later is in entries
    /// after it (to watch for what a command makes the game write).
    /// </summary>
    public long Mark()
    {
        lock (_lock)
        {
            if (_entries.Count > _start)
            {
                _entries[^1].Seal();
            }

            return _next - 1;
        }
    }

    public void Append(string text, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        TaskCompletionSource appended;
        lock (_lock)
        {
            var offset = 0;
            while (offset < text.Length)
            {
                var tail = _entries.Count > _start ? _entries[^1] : null;
                if (tail is null || tail.Text is not null || at - tail.At > MergeWindow || tail.Length >= MaxEntryLength)
                {
                    tail = new Entry(_next++, at);
                    _entries.Add(tail);
                }

                var take = Math.Min(MaxEntryLength - tail.Length, text.Length - offset);
                // Never split a surrogate pair between two entries.
                if (take < text.Length - offset && take > 0 && char.IsHighSurrogate(text[offset + take - 1]))
                {
                    take--;
                }

                if (take == 0)
                {
                    tail.Seal();
                    continue;
                }

                tail.Builder!.Append(text, offset, take);
                offset += take;
                _length += take;
            }

            Evict();
            appended = _appended;
            _appended = NewSignal();
        }

        appended.TrySetResult();
    }

    /// <summary>
    /// Entries after <paramref name="after"/> (all of them when null), up to about <paramref name="maxLength"/>
    /// characters (at least one entry). When output after <paramref name="after"/> was already evicted, a gap entry
    /// comes first.
    /// </summary>
    public IReadOnlyList<ConsoleEntry> Read(long? after, int maxLength)
    {
        lock (_lock)
        {
            var result = new List<ConsoleEntry>();
            if (_entries.Count == _start)
            {
                return result;
            }

            var first = _entries[_start];
            var index = _start;
            if (after is { } last)
            {
                if (last >= _next - 1)
                {
                    return result;
                }

                if (last < first.Sequence - 1)
                {
                    result.Add(new ConsoleEntry(first.Sequence - 1, first.At, "", IsGap: true));
                }
                else
                {
                    index = _start + (int)(last + 1 - first.Sequence);
                }
            }

            var length = 0;
            for (; index < _entries.Count; index++)
            {
                var entry = _entries[index];
                if (length > 0 && length + entry.Length > maxLength)
                {
                    break;
                }

                result.Add(new ConsoleEntry(entry.Sequence, entry.At, entry.Seal()));
                length += entry.Length;
            }

            return result;
        }
    }

    /// <summary>Completes once there is an entry after <paramref name="after"/> (any entry when null).</summary>
    public Task WaitAsync(long? after, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _entries.Count > _start && (after is not { } last || last < _next - 1)
                ? Task.CompletedTask
                : _appended.Task.WaitAsync(cancellationToken);
        }
    }

    private void Evict()
    {
        while (_length > _capacity && _entries.Count - _start > 1)
        {
            _length -= _entries[_start].Length;
            _entries[_start] = null!;
            _start++;
        }

        if (_start > 1024 && _start > _entries.Count / 2)
        {
            _entries.RemoveRange(0, _start);
            _start = 0;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Entry(long sequence, DateTimeOffset at)
    {
        public long Sequence { get; } = sequence;

        public DateTimeOffset At { get; } = at;

        public StringBuilder? Builder { get; private set; } = new();

        /// <summary>Set once the entry was read: it no longer changes.</summary>
        public string? Text { get; private set; }

        public int Length => Text?.Length ?? Builder!.Length;

        public string Seal()
        {
            if (Text is null)
            {
                Text = Builder!.ToString();
                Builder = null;
            }

            return Text;
        }
    }
}
