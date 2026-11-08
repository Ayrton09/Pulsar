using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// The URLs a game takes with Windows' HTTP Server API (docs/architecture/06, "HTTP URLs", Pulsar 1.0.35): a port the
/// manifest marks <c>http</c> is the game's <c>http://+:port/</c>, which only an administrator may listen on unless the URL
/// is reserved for the account. A server's account is none, so the agent reserves it for that account alone before every
/// start, and releases it when the server no longer has that port, is deleted or its node unenrolled.
/// </summary>
public interface IHttpUrlReservations
{
    /// <summary>
    /// Reserves <c>http://+:port/</c> for the server's account on each of <paramref name="ports"/> and releases the
    /// server's earlier reservations it no longer has. What could not be done comes back as lines for the server's console
    /// (the game then answers on its machine only); nothing of it stops the start.
    /// </summary>
    IReadOnlyList<string> Reserve(string shortId, IReadOnlyList<int> ports, ProcessIdentity identity);

    /// <summary>Releases the server's reservations (it is deleted, or moves to another game).</summary>
    void Remove(string shortId);

    /// <summary>Releases every server's reservations (the node is unenrolled): how many.</summary>
    int RemoveAll();
}

/// <summary>Nothing to reserve: an agent that does not run games with Windows' HTTP Server API, and tests.</summary>
public sealed class NoHttpUrlReservations : IHttpUrlReservations
{
    public IReadOnlyList<string> Reserve(string shortId, IReadOnlyList<int> ports, ProcessIdentity identity) => [];

    public void Remove(string shortId)
    {
    }

    public int RemoveAll() => 0;
}

/// <summary>The URL reservations of the HTTP Server API (Windows' <c>netsh http ... urlacl</c>), by URL.</summary>
public interface IHttpUrlAcls
{
    /// <summary>
    /// The security descriptor (SDDL) the URL is reserved with, as the system writes it back (<see cref="Canonical"/>), or
    /// null when it is not reserved.
    /// </summary>
    string? Find(string url);

    /// <summary>
    /// The security descriptor (SDDL) as the system writes it back once reserved: a well-known account by its alias (the
    /// machine's Administrator as <c>LA</c>), not by the security identifier it was given. What <see cref="Find"/> returns
    /// is compared with this form.
    /// </summary>
    string Canonical(string securityDescriptor);

    /// <summary>Reserves the URL with the security descriptor (SDDL).</summary>
    void Add(string url, string securityDescriptor);

    /// <summary>Releases the URL's reservation (nothing when there is none).</summary>
    void Delete(string url);
}

/// <summary>
/// <see cref="IHttpUrlReservations"/> over the system's reservations (<paramref name="acls"/>). Each server's are recorded
/// in <paramref name="recordFile"/> (the agent's state, out of the servers' reach), each with the descriptor it was made
/// with, so only what the agent made, as it made it, is ever released or replaced: a URL reserved by anyone else stays as
/// it is, and the server's console says why its game answers on the node only. Reserving and releasing take turns.
/// </summary>
/// <param name="sidOf">The security identifier of the account a server's game runs as.</param>
/// <param name="isServer">
/// Whether a short id is a server of this agent now: a reservation the agent made for one that is not (deleted while its
/// reservation could not be released) is taken over by the server that has the port now, never a live server's. Without
/// it, none is taken over.
/// </param>
public sealed partial class HttpUrlReservations(IHttpUrlAcls acls, Func<ProcessIdentity, string> sidOf, string recordFile,
    ILogger<HttpUrlReservations> logger, Func<string, bool>? isServer = null) : IHttpUrlReservations
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();

    private enum Released
    {
        Done,

        // Not the agent's (any more): someone else's, or gone.
        NotOurs,

        // The system refused: still the agent's, kept in the record to be released later.
        Failed,
    }

    /// <summary>The URL a game listens on, on every address, at <paramref name="port"/>.</summary>
    public static string Url(int port) => $"http://+:{port}/";

    /// <summary>Listening (generic execute) for that account alone, as <c>netsh http add urlacl ... user=&lt;account&gt;</c> writes it.</summary>
    public static string Descriptor(string sid) => $"D:(A;;GX;;;{sid})";

    public IReadOnlyList<string> Reserve(string shortId, IReadOnlyList<int> ports, ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(ports);
        lock (_lock)
        {
            var records = Load();
            var wanted = ports.Distinct().Order().Select(Url).ToList();
            var problems = new List<string>();
            string descriptor;
            try
            {
                descriptor = acls.Canonical(Descriptor(sidOf(identity)));
            }
            catch (Exception ex) when (ex is SystemException)
            {
                return wanted.Count == 0 ? [] : [$"The game's web ports could not be opened to other machines: the server's account was not found ({ex.Message})."];
            }

            var mine = new List<Entry>(records.GetValueOrDefault(shortId) ?? []);

            // Recorded before anything is reserved: a stop in between leaves a record of what may be the agent's (released
            // later only if it still has that descriptor), never a reservation of the agent's that no record knows.
            records[shortId] = [.. mine, .. wanted.Where(url => !mine.Any(e => e.Is(url, descriptor))).Select(url => new Entry(url, descriptor))];
            TrySave(records);

            // What this server had and no longer has (or had for an account it no longer runs as) goes first: another server
            // may get that port now. What the system refuses to release stays recorded.
            foreach (var entry in mine.Where(e => !e.Is(e.Url, descriptor) || !wanted.Contains(e.Url, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                if (Release(entry) != Released.Failed)
                {
                    mine.Remove(entry);
                }
            }

            // Only a server's own account's reservation made by someone else is taken as the agent's (Pulsar made that
            // account, and deletes it with the server); one for the agent's own user (a development agent's) never is.
            var adopt = identity is ProcessIdentity.AccountIdentity;
            foreach (var url in wanted)
            {
                if (mine.Any(e => e.Is(url, descriptor)) && Present(url, descriptor) is true)
                {
                    continue;
                }

                try
                {
                    var found = acls.Find(url);
                    if (found is not null && !Same(found, descriptor))
                    {
                        var owner = records.FirstOrDefault(r => r.Key != shortId && r.Value.Any(e => e.Is(url, found)));
                        if (owner.Key is not null && isServer?.Invoke(owner.Key) == false)
                        {
                            // A server this agent no longer has, whose reservation could not be released: the port is this one's now.
                            acls.Delete(url);
                            records[owner.Key] = [.. owner.Value.Where(e => !e.Is(url, found))];
                            found = null;
                        }
                        else
                        {
                            problems.Add(owner.Key is not null
                                ? $"{url} is reserved for another game server of this machine that has port {url[9..^1]} on another address, so this game answers there on this machine only. Give one of them another port."
                                : $"{url} is reserved for another account on this machine ({found}), so the game answers there on this machine only. " +
                                    $"Remove that reservation (netsh http delete urlacl url={url}) or give the server another port.");
                            continue;
                        }
                    }

                    if (found is null)
                    {
                        acls.Add(url, descriptor);
                        LogReserved(shortId, url);
                        mine.Add(new Entry(url, descriptor));
                    }
                    else if (adopt && !mine.Any(e => e.Is(url, descriptor)))
                    {
                        mine.Add(new Entry(url, descriptor));
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{url} could not be reserved for the server's account ({ex.Message}), so the game answers there on this machine only.");
                }
            }

            Keep(records, shortId, mine);
            TrySave(records);
            return problems;
        }
    }

    public void Remove(string shortId)
    {
        lock (_lock)
        {
            var records = Load();
            if (!records.TryGetValue(shortId, out var entries))
            {
                return;
            }

            // What the system refused to release stays recorded, to be released another time.
            Keep(records, shortId, [.. entries.Where(e => Release(e) == Released.Failed)]);
            TrySave(records);
        }
    }

    public int RemoveAll()
    {
        lock (_lock)
        {
            var records = Load();
            var count = 0;
            foreach (var (shortId, entries) in records.ToList())
            {
                var left = new List<Entry>();
                foreach (var entry in entries)
                {
                    switch (Release(entry))
                    {
                        case Released.Done:
                            count++;
                            break;
                        case Released.Failed:
                            left.Add(entry);
                            break;
                    }
                }

                Keep(records, shortId, left);
            }

            TrySave(records);
            return count;
        }
    }

    private static void Keep(Dictionary<string, IReadOnlyList<Entry>> records, string shortId, List<Entry> entries)
    {
        if (entries.Count > 0)
        {
            records[shortId] = entries;
        }
        else
        {
            records.Remove(shortId);
        }
    }

    // Whether the URL is reserved with the descriptor now (null: the system could not say).
    private bool? Present(string url, string descriptor)
    {
        try
        {
            return acls.Find(url) is { } found && Same(found, descriptor);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Only the reservation the agent made, as it made it: one changed since (someone else's now) stays.
    private Released Release(Entry entry)
    {
        try
        {
            if (acls.Find(entry.Url) is not { } found || !Same(found, entry.Descriptor))
            {
                return Released.NotOurs;
            }

            acls.Delete(entry.Url);
            LogReleased(entry.Url);
            return Released.Done;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            LogReleaseFailed(ex, entry.Url);
            return Released.Failed;
        }
    }

    private static bool Same(string found, string descriptor) => string.Equals(found.Trim(), descriptor.Trim(), StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, IReadOnlyList<Entry>> Load()
    {
        if (!File.Exists(recordFile))
        {
            return new Dictionary<string, IReadOnlyList<Entry>>(StringComparer.Ordinal);
        }

        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, List<Entry>>>(File.ReadAllBytes(recordFile), s_json) ?? [];
            return read.Where(r => r.Value is not null).ToDictionary(r => r.Key,
                r => (IReadOnlyList<Entry>)[.. r.Value.Where(e => e is { Url: not null, Descriptor: not null })], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: nothing in it is taken as the agent's, and it is set aside (not written over) for whoever looks.
            LogRecordUnreadable(ex, recordFile);
            try
            {
                File.Move(recordFile, $"{recordFile}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            }
            catch (Exception moveFailed) when (moveFailed is IOException or UnauthorizedAccessException)
            {
                LogRecordUnreadable(moveFailed, recordFile);
            }

            return new Dictionary<string, IReadOnlyList<Entry>>(StringComparer.Ordinal);
        }
    }

    // A record that cannot be written stops nothing (a start, a deletion): a server's reservations are found again by
    // their descriptor at its next start.
    private void TrySave(Dictionary<string, IReadOnlyList<Entry>> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordFile)!);
            var temporary = recordFile + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(records, s_json));
            File.Move(temporary, recordFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogRecordNotSaved(ex, recordFile);
        }
    }

    /// <summary>One URL the agent reserved, with the descriptor it reserved it with.</summary>
    public sealed record Entry(string Url, string Descriptor)
    {
        public bool Is(string url, string descriptor) => Url.Equals(url, StringComparison.OrdinalIgnoreCase) && Same(Descriptor, descriptor);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reserved {Url} for server {ShortId}'s account")]
    private partial void LogReserved(string shortId, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released {Url}")]
    private partial void LogReleased(string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release {Url}: it stays recorded, to be released later")]
    private partial void LogReleaseFailed(Exception ex, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The record of reserved URLs ({Path}) could not be read: it is set aside and nothing in it is released")]
    private partial void LogRecordUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The record of reserved URLs ({Path}) could not be written")]
    private partial void LogRecordNotSaved(Exception ex, string path);
}
