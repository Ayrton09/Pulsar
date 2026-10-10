namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// The recent output of a game in its host (docs/architecture/06, "Server hosts"), addressed by character offsets
/// since the game started: whoever reads remembers where it stopped and asks from there, across connections. Bounded:
/// the oldest output leaves first, and a reader that asks for it learns what it missed.
/// </summary>
public sealed class OutputRing
{
    private readonly Lock _lock = new();
    private readonly List<OutputPiece> _pieces = [];
    private readonly int _capacity;
    private int _first; // pieces before it were evicted (removed from the list in batches)
    private int _length; // characters kept
    private long _start;
    private long _end;
    private bool _completed;
    private TaskCompletionSource _changed = NewSignal();

    public OutputRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1024);
        _capacity = capacity;
    }

    /// <summary>The oldest offset still kept.</summary>
    public long Start
    {
        get
        {
            lock (_lock)
            {
                return _start;
            }
        }
    }

    /// <summary>The offset right after the newest output.</summary>
    public long End
    {
        get
        {
            lock (_lock)
            {
                return _end;
            }
        }
    }

    /// <summary>Whether the output closed (nothing more will be appended).</summary>
    public bool Completed
    {
        get
        {
            lock (_lock)
            {
                return _completed;
            }
        }
    }

    public void Append(string text, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        TaskCompletionSource changed;
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            // A piece larger than the whole ring keeps only its end.
            var offset = _end;
            if (text.Length > _capacity)
            {
                offset += text.Length - _capacity;
                text = text[^_capacity..];
            }

            _pieces.Add(new OutputPiece(offset, at, text));
            _end = offset + text.Length;
            _length += text.Length;
            while (_length > _capacity)
            {
                _length -= _pieces[_first].Text.Length;
                _first++;
            }

            _start = _pieces[_first].Offset;
            if (_first > 1024 && _first > _pieces.Count / 2)
            {
                _pieces.RemoveRange(0, _first);
                _first = 0;
            }

            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
    }

    /// <summary>No more output: readers that reached the end learn it.</summary>
    public void Complete()
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            _completed = true;
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
    }

    /// <summary>
    /// Output from <paramref name="from"/> on, at most about <paramref name="maxCharacters"/> of it (always at least one
    /// piece when there is any). <c>Lost</c> is set when output from <paramref name="from"/> was already evicted: the
    /// pieces then start at <c>Lost.To</c>.
    /// </summary>
    public OutputRead Read(long from, int maxCharacters)
    {
        lock (_lock)
        {
            (long From, long To)? lost = null;
            if (from < _start)
            {
                lost = (from, _start);
                from = _start;
            }

            var pieces = new List<OutputPiece>();
            if (from >= _end)
            {
                return new OutputRead(pieces, lost);
            }

            var index = Find(from);
            var taken = 0;
            for (var i = index; i < _pieces.Count && (taken < maxCharacters || pieces.Count == 0); i++)
            {
                var piece = _pieces[i];
                if (piece.Offset < from)
                {
                    // The reader stopped inside this piece.
                    piece = piece with { Offset = from, Text = piece.Text[(int)(from - piece.Offset)..] };
                }

                if (piece.Text.Length > maxCharacters)
                {
                    var take = maxCharacters;
                    if (char.IsHighSurrogate(piece.Text[take - 1]))
                    {
                        take--;
                    }

                    piece = piece with { Text = piece.Text[..take] };
                }

                pieces.Add(piece);
                taken += piece.Text.Length;
                if (piece.Offset + piece.Text.Length < _pieces[i].Offset + _pieces[i].Text.Length)
                {
                    break; // cut short: the rest of it comes with the next read
                }
            }

            return new OutputRead(pieces, lost);
        }
    }

    /// <summary>Completes once there is output after <paramref name="from"/>, or the output closed.</summary>
    public Task WaitAsync(long from, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _end > from || _completed ? Task.CompletedTask : _changed.Task.WaitAsync(cancellationToken);
        }
    }

    // The piece that holds offset (call holding the lock, with _start <= offset < _end).
    private int Find(long offset)
    {
        int low = _first, high = _pieces.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_pieces[middle].Offset <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record OutputPiece(long Offset, DateTimeOffset At, string Text);

public sealed record OutputRead(IReadOnlyList<OutputPiece> Pieces, (long From, long To)? Lost);
