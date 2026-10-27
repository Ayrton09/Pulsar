namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Where a server's console output goes: the in-memory buffer browsers watch and, when there is one, the rotated log on
/// disk. One per server, across restarts of its game. Everything written is masked first (<see cref="ConsoleMask"/>).
/// </summary>
public sealed class ServerConsole(ConsoleBuffer buffer, ConsoleLog? log = null) : IAsyncDisposable
{
    private readonly ConsoleMask _mask = new();
    private readonly Lock _lock = new();

    public ConsoleBuffer Buffer { get; } = buffer;

    public void Write(string text, DateTimeOffset at)
    {
        lock (_lock)
        {
            Emit(_mask.Apply(text), at);
        }
    }

    /// <summary>A line from the agent itself (an RCON error, say), dimmed so it stands apart from the game's output.</summary>
    public void Note(string text, DateTimeOffset at) => Write($"\u001b[2m[Pulsar] {text}\u001b[0m\r\n", at);

    /// <summary>The secrets the console hides from now on: those of the run of the game that starts.</summary>
    public void Hide(IEnumerable<string> secrets, DateTimeOffset at)
    {
        lock (_lock)
        {
            Emit(_mask.Use(secrets), at);
        }
    }

    /// <summary>Writes what the mask held back, waiting to see whether it began a secret (the game exited).</summary>
    public void Flush(DateTimeOffset at)
    {
        lock (_lock)
        {
            Emit(_mask.Flush(), at);
        }
    }

    public ValueTask DisposeAsync() => log?.DisposeAsync() ?? ValueTask.CompletedTask;

    private void Emit(string text, DateTimeOffset at)
    {
        if (text.Length == 0)
        {
            return;
        }

        Buffer.Append(text, at);
        log?.Write(text, at);
    }
}
