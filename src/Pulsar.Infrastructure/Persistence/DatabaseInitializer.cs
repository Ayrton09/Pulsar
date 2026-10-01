using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pulsar.Domain.Access;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>State of the database after initialization.</summary>
/// <param name="HasUsers">False on a fresh installation: the panel then waits for the initial setup.</param>
public sealed record DatabaseStatus(bool HasUsers);

/// <summary>Prepares the database before the panel accepts requests.</summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Checks connectivity and the PostgreSQL version, applies migrations (when <paramref name="applyMigrations"/> is true)
    /// and creates the built-in data. Throws <see cref="DatabaseStartupException"/> with an actionable message.
    /// </summary>
    Task<DatabaseStatus> InitializeAsync(bool applyMigrations, CancellationToken cancellationToken);
}

internal sealed partial class DatabaseInitializer(
    PulsarDbContext db, TimeProvider clock, ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task<DatabaseStatus> InitializeAsync(bool applyMigrations, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        var where = $"{builder.Host}:{builder.Port}, database '{builder.Database}', user '{builder.Username}'";

        await CheckServerAsync(where, cancellationToken);

        // EF Core takes a database-wide migration lock, so several panel instances starting together are safe.
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            if (!applyMigrations)
            {
                throw new DatabaseStartupException(
                    $"The database schema is out of date ({pending.Count} pending migrations: {string.Join(", ", pending)}). " +
                    "Run 'Pulsar.Api migrate' or set Database:ApplyMigrationsOnStartup to true.");
            }

            LogApplyingMigrations(pending.Count, pending);
            await db.Database.MigrateAsync(cancellationToken);
        }

        await EnsureAdministratorRoleAsync(cancellationToken);
        return new DatabaseStatus(HasUsers: await db.Users.AnyAsync(cancellationToken));
    }

    private async Task CheckServerAsync(string where, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException or TimeoutException)
        {
            throw new DatabaseStartupException(
                $"Cannot connect to PostgreSQL ({where}): {ex.GetBaseException().Message.TrimEnd('.')}. " +
                "Check ConnectionStrings:Pulsar and that the server is running.", ex);
        }

        try
        {
            var version = connection.PostgreSqlVersion;
            if (version.Major < DatabaseOptions.MinimumPostgresMajorVersion)
            {
                throw new DatabaseStartupException(
                    $"PostgreSQL {version} is not supported ({where}); version {DatabaseOptions.MinimumPostgresMajorVersion} or newer is required.");
            }

            LogConnected(version, where);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task EnsureAdministratorRoleAsync(CancellationToken cancellationToken)
    {
        if (await db.Roles.AnyAsync(r => r.IsSystem, cancellationToken))
        {
            return;
        }

        db.Roles.Add(Role.CreateAdministrator(clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        LogAdministratorRoleCreated();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to PostgreSQL {Version} ({Where})")]
    private partial void LogConnected(Version version, string where);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} database migrations: {Migrations}")]
    private partial void LogApplyingMigrations(int count, IReadOnlyList<string> migrations);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the built-in Administrator role")]
    private partial void LogAdministratorRoleCreated();
}
