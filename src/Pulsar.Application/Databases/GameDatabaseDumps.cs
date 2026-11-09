namespace Pulsar.Application.Databases;

/// <summary>A game's database as its own role reaches it from the panel (the password in clear, for this call only).</summary>
/// <param name="Host">The host's address for the panel (not the one for game servers).</param>
/// <param name="Name">The database, and its role.</param>
public sealed record GameDatabaseTarget(string Host, int Port, string Name, string Password, bool RequireTls)
{
    // Never in logs or exception messages.
    public override string ToString() => $"{Name}@{Host}:{Port}";
}

/// <summary>A dump in a file of the panel's: its caller deletes it.</summary>
public sealed record GameDatabaseDump(string Path, long Size, string Sha256);

/// <summary>A dump or a restore that did not happen (the database host's or the program's words).</summary>
public sealed class GameDatabaseDumpException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Dumps and restores a game's PostgreSQL database for its server's backups (1.0.37; Infrastructure, with PostgreSQL's
/// own programs on the panel's machine).
/// </summary>
public interface IGameDatabaseDumps
{
    /// <summary>The database's dump, in a new file only the panel's account opens.</summary>
    /// <exception cref="GameDatabaseDumpException">It could not be made.</exception>
    Task<GameDatabaseDump> DumpAsync(GameDatabaseTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the database with the dump <paramref name="content"/> holds (exactly <paramref name="length"/> bytes), never
    /// leaving it half restored: the dump goes into a new database first, which takes the old one's place once whole.
    /// <paramref name="admin"/> is the host's account. <paramref name="receiving"/> covers the content's arrival only,
    /// and <paramref name="received"/> is asked once all of it is in: false (whoever sent it gave up) restores nothing;
    /// otherwise the restore has its own time limit.
    /// </summary>
    /// <exception cref="GameDatabaseDumpException">It could not be restored; the database is as it was.</exception>
    Task RestoreAsync(GameDatabaseTarget target, DatabaseHostConnection admin, Stream content, long length, Func<bool> received,
        CancellationToken receiving);
}
