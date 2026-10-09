using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;

namespace Pulsar.Application.Databases;

/// <summary>Encrypts database passwords for the panel's database with its Data Protection key ring (docs/architecture/08).</summary>
public interface IDatabaseSecretProtector
{
    byte[] Protect(string value);

    /// <exception cref="CryptographicException">The key ring that encrypted it is gone.</exception>
    string Unprotect(byte[] protectedValue);
}

/// <summary>How the panel reaches a database host (the password in clear, for this call only).</summary>
public sealed record DatabaseHostConnection(string Host, int Port, string UserName, string Password)
{
    // Never in logs or exception messages.
    public override string ToString() => $"{UserName}@{Host}:{Port}";
}

/// <summary>A MySQL/MariaDB server's answer the panel could not work with (unreachable, refused, not allowed…).</summary>
public sealed class DatabaseHostException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Talks to MySQL/MariaDB (Infrastructure, MySqlConnector). Names come from <see cref="ServerDatabase.NameFor"/> and
/// passwords from <see cref="ServerDatabase.NewPassword"/>; the client checks them again before quoting them into SQL.
/// </summary>
public interface IDatabaseHostClient
{
    /// <summary>Connects and returns the server's version.</summary>
    /// <exception cref="DatabaseHostException">It could not.</exception>
    Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the database and a user of the same name allowed to use it alone. Refused when the database already
    /// exists (never adopts data it did not create); what it created is removed again when a later step fails.
    /// </summary>
    Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken);

    /// <summary>Drops the database and its user (either may already be gone).</summary>
    Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken);

    Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken);
}

/// <param name="Password">Null on an update keeps the current password.</param>
/// <param name="NodeIds">The nodes whose servers may use it; empty: every node.</param>
public sealed record DatabaseHostDefinition(
    string Name,
    string Host,
    int Port,
    string UserName,
    string? Password,
    string? ServerAddress,
    int? ServerPort,
    string? UserHost,
    IReadOnlyList<Guid> NodeIds);

/// <param name="Databases">Databases servers have on it.</param>
public sealed record DatabaseHostView(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string UserName,
    string? ServerAddress,
    int? ServerPort,
    string UserHost,
    IReadOnlyList<Guid> NodeIds,
    int Databases,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DatabaseHostList(IReadOnlyList<DatabaseHostView> Hosts);

/// <param name="Version">What the server says it is (e.g. "12.3.3-MariaDB").</param>
public sealed record DatabaseHostCheck(string Version);

/// <summary>
/// Database hosts (Phase 13), for holders of <c>settings.manage</c>. The password goes in encrypted and never comes
/// back out. A host is saved only once the panel could connect with what was given.
/// </summary>
public sealed class DatabaseHosts(IPulsarDbContext db, IDatabaseSecretProtector secrets, IDatabaseHostClient client, IAuditLog audit,
    TimeProvider clock)
{
    public async Task<DatabaseHostList> ListAsync(CancellationToken cancellationToken)
    {
        var hosts = await db.DatabaseHosts.AsNoTracking().OrderBy(h => h.Name).ToListAsync(cancellationToken);
        var counts = await db.ServerDatabases.AsNoTracking().GroupBy(d => d.HostId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return new DatabaseHostList([.. hosts.Select(h => View(h, counts.FirstOrDefault(c => c.Id == h.Id)?.Count ?? 0))]);
    }

    public async Task<Result<DatabaseHostView>> CreateAsync(DatabaseHostDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (await db.DatabaseHosts.CountAsync(cancellationToken) >= DatabaseHost.MaxHosts)
        {
            return DatabaseErrors.TooManyHosts;
        }

        var password = Password(definition);
        var created = DatabaseHost.Create(definition.Name, definition.Host, definition.Port, definition.UserName,
            password is null ? null : secrets.Protect(password), definition.ServerAddress, definition.ServerPort, definition.UserHost,
            definition.NodeIds ?? [], clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var host = created.Value;
        if (await CheckAsync(new DatabaseHostConnection(host.Host, host.Port, host.UserName, password!), cancellationToken) is { } refused)
        {
            return refused;
        }

        db.DatabaseHosts.Add(host);
        audit.Record(AuditActions.DatabaseHost.Created, metadata: Metadata(host, passwordChanged: true)).WithTarget("database_host", host.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return View(host, 0);
    }

    public async Task<Result<DatabaseHostView>> UpdateAsync(Guid id, DatabaseHostDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var host = await db.DatabaseHosts.SingleOrDefaultAsync(h => h.Id == new DatabaseHostId(id), cancellationToken);
        if (host is null)
        {
            return DatabaseErrors.HostNotFound;
        }

        var password = Password(definition);
        if (password is null && Connection(host) is null)
        {
            return DatabaseErrors.HostPasswordRequired; // the stored one cannot be read any more: it must be given again
        }

        var changed = host.Change(definition.Name, definition.Host, definition.Port, definition.UserName,
            password is null ? null : secrets.Protect(password), definition.ServerAddress, definition.ServerPort, definition.UserHost,
            definition.NodeIds ?? [], clock.GetUtcNow());
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        if (await CheckAsync(Connection(host)!, cancellationToken) is { } refused)
        {
            return refused;
        }

        audit.Record(AuditActions.DatabaseHost.Updated, metadata: Metadata(host, password is not null)).WithTarget("database_host", host.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return View(host, await db.ServerDatabases.CountAsync(d => d.HostId == host.Id, cancellationToken));
    }

    /// <summary>Only a host no server has a database on: Pulsar would lose track of them.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var host = await db.DatabaseHosts.SingleOrDefaultAsync(h => h.Id == new DatabaseHostId(id), cancellationToken);
        if (host is null)
        {
            return DatabaseErrors.HostNotFound;
        }

        if (await db.ServerDatabases.AnyAsync(d => d.HostId == host.Id, cancellationToken))
        {
            return DatabaseErrors.HostInUse;
        }

        db.DatabaseHosts.Remove(host);
        audit.Record(AuditActions.DatabaseHost.Deleted, metadata: new { host.Name, host.Host, host.Port }).WithTarget("database_host", host.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Connects with the saved settings.</summary>
    public async Task<Result<DatabaseHostCheck>> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        var host = await db.DatabaseHosts.AsNoTracking().SingleOrDefaultAsync(h => h.Id == new DatabaseHostId(id), cancellationToken);
        if (host is null)
        {
            return DatabaseErrors.HostNotFound;
        }

        if (Connection(host) is not { } connection)
        {
            return DatabaseErrors.HostPasswordUnreadable;
        }

        try
        {
            return new DatabaseHostCheck(await client.CheckAsync(connection, cancellationToken));
        }
        catch (DatabaseHostException ex)
        {
            return DatabaseErrors.Unreachable(ex.Message);
        }
    }

    /// <summary>How the panel reaches <paramref name="host"/>; null when its password cannot be read with this panel's keys.</summary>
    public DatabaseHostConnection? Connection(DatabaseHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            return new DatabaseHostConnection(host.Host, host.Port, host.UserName, secrets.Unprotect(host.PasswordProtected));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private async Task<DomainError?> CheckAsync(DatabaseHostConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await client.CheckAsync(connection, cancellationToken);
            return null;
        }
        catch (DatabaseHostException ex)
        {
            return DatabaseErrors.Unreachable(ex.Message);
        }
    }

    private static string? Password(DatabaseHostDefinition definition) => string.IsNullOrEmpty(definition.Password) ? null : definition.Password;

    private static object Metadata(DatabaseHost host, bool passwordChanged) =>
        new { host.Name, host.Host, host.Port, host.UserName, host.ServerAddress, host.ServerPort, host.UserHost, host.NodeIds, passwordChanged };

    private static DatabaseHostView View(DatabaseHost host, int databases) => new(host.Id.Value, host.Name, host.Host, host.Port, host.UserName,
        host.ServerAddress, host.ServerPort, host.UserHost, host.NodeIds, databases, host.CreatedAt, host.UpdatedAt);
}
