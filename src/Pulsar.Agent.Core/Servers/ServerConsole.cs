namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Where a server's console output goes: the in-memory buffer browsers watch and, when there is one, the rotated log on
/// disk. One per server, across restarts of its game.
/// </summary>
public sealed class ServerConsole(ConsoleBuffer buffer, ConsoleLog? log = null) : IAsyncDisposable
{
    public ConsoleBuffer Buffer { get; } = buffer;

    public void Write(string text, DateTimeOffset at)
    {
        Buffer.Append(text, at);
        log?.Write(text, at);
    }

    /// <summary>A line from the agent itself (an RCON error, say), dimmed so it stands apart from the game's output.</summary>
    public void Note(string text, DateTimeOffset at) => Write($"\u001b[2m[Pulsar] {text}\u001b[0m\r\n", at);

    public ValueTask DisposeAsync() => log?.DisposeAsync() ?? ValueTask.CompletedTask;
}
