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
    /// <summary>The security descriptor (SDDL) the URL is reserved with, or null when it is not reserved.</summary>
    string? Find(string url);

    /// <summary>Reserves the URL with the security descriptor (SDDL).</summary>
    void Add(string url, string securityDescriptor);

    /// <summary>Releases the URL's reservation (nothing when there is none).</summary>
    void Delete(string url);
}

/// <summary>
/// <see cref="IHttpUrlReservations"/> over the system's reservations (<paramref name="acls"/>). Each server's are recorded
/// in <paramref name="recordFile"/> (the agent's state, out of the servers' reach), so only what the agent made, with the
/// descriptor it made, is ever released or replaced: a URL reserved by anyone else stays as it is, and the server's
/// console says why its game answers on the node only. Reserving and releasing take turns.
/// </summary>
/// <param name="sidOf">The security identifier of the account a server's game runs as.</param>
public sealed partial class HttpUrlReservations(IHttpUrlAcls acls, Func<ProcessIdentity, string> sidOf, string recordFile,
    ILogger<HttpUrlReservations> logger) : IHttpUrlReservations
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();

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
                descriptor = Descriptor(sidOf(identity));
            }
            catch (Exception ex) when (ex is SystemException)
            {
                return wanted.Count == 0 ? [] : [$"The game's web ports could not be opened to other machines: the server's account was not found ({ex.Message})."];
            }

            // What this server had and no longer has goes first: another server may get that port now.
            if (records.TryGetValue(shortId, out var earlier))
            {
                foreach (var url in earlier.Urls.Except(wanted, StringComparer.OrdinalIgnoreCase))
                {
                    Release(url, earlier.Descriptor);
                }
            }

            var reserved = new List<string>();
            foreach (var url in wanted)
            {
                try
                {
                    var found = acls.Find(url);
                    if (found is not null && !Same(found, descriptor))
                    {
                        // Another server's, left by one deleted while this agent was away: the port is this server's now.
                        var stale = records.FirstOrDefault(r => r.Key != shortId && r.Value.Urls.Contains(url, StringComparer.OrdinalIgnoreCase)
                            && Same(found, r.Value.Descriptor));
                        if (stale.Key is null)
                        {
                            problems.Add($"{url} is reserved for another account on this machine ({found}), so the game answers there on this machine only. " +
                                $"Remove that reservation (netsh http delete urlacl url={url}) or give the server another port.");
                            continue;
                        }

                        acls.Delete(url);
                        records[stale.Key] = stale.Value with { Urls = [.. stale.Value.Urls.Where(u => !u.Equals(url, StringComparison.OrdinalIgnoreCase))] };
                        found = null;
                    }

                    if (found is null)
                    {
                        acls.Add(url, descriptor);
                        LogReserved(shortId, url);
                    }

                    reserved.Add(url);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{url} could not be reserved for the server's account ({ex.Message}), so the game answers there on this machine only.");
                }
            }

            if (reserved.Count > 0)
            {
                records[shortId] = new Record(descriptor, reserved);
            }
            else
            {
                records.Remove(shortId);
            }

            Save(records);
            return problems;
        }
    }

    public void Remove(string shortId)
    {
        lock (_lock)
        {
            var records = Load();
            if (!records.Remove(shortId, out var record))
            {
                return;
            }

            foreach (var url in record.Urls)
            {
                Release(url, record.Descriptor);
            }

            Save(records);
        }
    }

    public int RemoveAll()
    {
        lock (_lock)
        {
            var records = Load();
            var count = 0;
            foreach (var record in records.Values)
            {
                foreach (var url in record.Urls)
                {
                    count += Release(url, record.Descriptor) ? 1 : 0;
                }
            }

            Save([]);
            return count;
        }
    }

    // Only the reservation the agent made: one changed since (someone else's now) stays.
    private bool Release(string url, string descriptor)
    {
        try
        {
            if (acls.Find(url) is { } found && Same(found, descriptor))
            {
                acls.Delete(url);
                LogReleased(url);
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            LogReleaseFailed(ex, url);
        }

        return false;
    }

    private static bool Same(string found, string descriptor) => string.Equals(found.Trim(), descriptor, StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, Record> Load()
    {
        try
        {
            if (!File.Exists(recordFile))
            {
                return new Dictionary<string, Record>(StringComparer.Ordinal);
            }

            var read = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllBytes(recordFile), s_json);
            return new Dictionary<string, Record>(read ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: nothing is known to be the agent's, so nothing is released or replaced.
            LogRecordUnreadable(ex, recordFile);
            return new Dictionary<string, Record>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, Record> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordFile)!);
        var temporary = recordFile + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(records, s_json));
        File.Move(temporary, recordFile, overwrite: true);
    }

    /// <summary>What the agent reserved for one server: the descriptor and the URLs.</summary>
    public sealed record Record(string Descriptor, IReadOnlyList<string> Urls);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reserved {Url} for server {ShortId}'s account")]
    private partial void LogReserved(string shortId, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released {Url}")]
    private partial void LogReleased(string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release {Url}")]
    private partial void LogReleaseFailed(Exception ex, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The record of reserved URLs ({Path}) could not be read: nothing is released")]
    private partial void LogRecordUnreadable(Exception ex, string path);
}
