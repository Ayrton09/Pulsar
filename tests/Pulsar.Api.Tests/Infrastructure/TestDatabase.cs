using System.Runtime.CompilerServices;
using Npgsql;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// Real PostgreSQL for integration tests. PULSAR_TEST_POSTGRES is a connection string without a database whose user
/// can create databases; every <see cref="CreateAsync"/> makes a fresh, uniquely named one. Without the variable the
/// database tests are skipped, unless PULSAR_REQUIRE_DATABASE=true (CI), where they fail instead.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private const string ConnectionVariable = "PULSAR_TEST_POSTGRES";

    private TestDatabase(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    /// <summary>True when database tests should run (used by <see cref="DatabaseFactAttribute"/>).</summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable))
        || string.Equals(Environment.GetEnvironmentVariable("PULSAR_REQUIRE_DATABASE"), "true", StringComparison.OrdinalIgnoreCase);

    public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var server = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new InvalidOperationException($"{ConnectionVariable} is not set (see CONTRIBUTING.md, development setup).");
        }

        var name = $"pulsar_test_{Guid.CreateVersion7():N}";
        await using (var connection = new NpgsqlConnection(WithDatabase(server, "postgres")))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new TestDatabase(name, WithDatabase(server, name));
    }

    public async ValueTask DisposeAsync()
    {
        // Pooled connections of the panel under test would keep the database busy.
        NpgsqlConnection.ClearAllPools();
        var server = Environment.GetEnvironmentVariable(ConnectionVariable)!;
        await using var connection = new NpgsqlConnection(WithDatabase(server, "postgres"));
        await connection.OpenAsync();
        // On Windows a drop waits for a checkpoint, which takes more than the default 30 s on a busy disk.
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection) { CommandTimeout = 120 };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege && attempt < 10)
            {
                // FORCE also tries to terminate an autovacuum worker busy with the database, which a role without
                // pg_signal_backend may not do ("permission denied to terminate process"); the worker finishes soon.
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
}

/// <summary>A fact that needs PostgreSQL; skipped when <see cref="TestDatabase.IsEnabled"/> is false.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Set PULSAR_TEST_POSTGRES to run database tests (see CONTRIBUTING.md).";
        SkipUnless = nameof(TestDatabase.IsEnabled);
        SkipType = typeof(TestDatabase);
    }
}

/// <summary>A theory that needs PostgreSQL; skipped when <see cref="TestDatabase.IsEnabled"/> is false.</summary>
public sealed class DatabaseTheoryAttribute : TheoryAttribute
{
    public DatabaseTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Set PULSAR_TEST_POSTGRES to run database tests (see CONTRIBUTING.md).";
        SkipUnless = nameof(TestDatabase.IsEnabled);
        SkipType = typeof(TestDatabase);
    }
}
