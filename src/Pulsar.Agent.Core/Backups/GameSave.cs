using System.Text.RegularExpressions;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Backups;

/// <summary>
/// Asks a running game to write its world to disk before a backup captures it (the manifest's <c>backup.save</c>;
/// docs/architecture/05, "Backups"). The commands go through the server's console like typed ones, so whoever watches
/// the console sees them and the game's answers.
/// </summary>
public static class GameSave
{
    private const int ReadLength = 64 * 1024;

    /// <summary>
    /// Sends the save commands and waits for the confirmation line, at most its timeout (or, for a game without one, the
    /// manifest's wait).
    /// </summary>
    public static async Task<GameSaveOutcome> SaveAsync(ServerSupervisor server, GameSaveDefinition save, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(save);
        var buffer = server.Console.Buffer;
        var mark = buffer.Mark();
        server.Console.Note("Asking the game to save before a backup", clock.GetUtcNow());
        foreach (var command in save.Commands)
        {
            if (await server.SendInputAsync(command) != AckStatus.Accepted)
            {
                return GameSaveOutcome.Unconfirmed; // it stopped meanwhile
            }
        }

        if (save.Confirmation is { } pattern)
        {
            var confirmation = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                TimeSpan.FromSeconds(1));
            return await ShowsAsync(buffer, mark, confirmation, TimeSpan.FromSeconds(save.TimeoutSeconds), clock, cancellationToken)
                ? GameSaveOutcome.Confirmed
                : GameSaveOutcome.Unconfirmed;
        }

        await Task.Delay(TimeSpan.FromSeconds(save.WaitSeconds), clock, cancellationToken);
        return GameSaveOutcome.Waited;
    }

    /// <summary>Sends the commands that undo what the save paused; a game that stopped meanwhile starts saving again by itself.</summary>
    public static async Task ResumeAsync(ServerSupervisor server, IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(commands);
        foreach (var command in commands)
        {
            _ = await server.SendInputAsync(command);
        }
    }

    /// <summary>Whether a console line after <paramref name="after"/> matches <paramref name="pattern"/> within <paramref name="timeout"/>.</summary>
    internal static async Task<bool> ShowsAsync(ConsoleBuffer buffer, long after, Regex pattern, TimeSpan timeout, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
        var lines = new ConsoleLines();
        var last = after;
        try
        {
            while (true)
            {
                await buffer.WaitAsync(last, linked.Token);
                foreach (var entry in buffer.Read(last, ReadLength))
                {
                    last = entry.Sequence;
                    foreach (var line in lines.Add(entry.Text))
                    {
                        if (Matches(pattern, line))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool Matches(Regex pattern, string line)
    {
        try
        {
            return pattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
