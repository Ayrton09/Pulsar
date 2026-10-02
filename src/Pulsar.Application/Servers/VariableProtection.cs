namespace Pulsar.Application.Servers;

/// <summary>
/// Encrypts secret variable values (GSLT, join passwords) for the database (docs/architecture/08, "Secrets"): ASP.NET
/// Core Data Protection in the panel. Without the panel's key ring, a database backup cannot reveal them.
/// </summary>
public interface IVariableProtector
{
    byte[] Protect(string value);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The key ring that encrypted it is gone.</exception>
    string Unprotect(byte[] protectedValue);
}

/// <summary>Live progress of running operations, kept in memory; the database gets it only now and then.</summary>
public sealed class OperationProgressCache(TimeProvider clock)
{
    /// <summary>How often progress within the same stage is written to the database (docs/architecture/02).</summary>
    public static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(15);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <summary>Records progress; returns whether it should also be written to the database now.</summary>
    public bool Update(Guid operationId, string stage, decimal? percent)
    {
        var now = clock.GetUtcNow();
        var persist = false;
        _entries.AddOrUpdate(operationId,
            _ =>
            {
                persist = true;
                return new Entry(stage, percent, now);
            },
            (_, previous) =>
            {
                persist = previous.Stage != stage || now - previous.PersistedAt >= PersistInterval;
                return new Entry(stage, percent, persist ? now : previous.PersistedAt);
            });
        return persist;
    }

    public (string Stage, decimal? Percent)? Get(Guid operationId) => _entries.TryGetValue(operationId, out var entry) ? (entry.Stage, entry.Percent) : null;

    public void Remove(Guid operationId) => _entries.TryRemove(operationId, out _);

    private sealed record Entry(string Stage, decimal? Percent, DateTimeOffset PersistedAt);
}
