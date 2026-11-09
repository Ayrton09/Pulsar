using System.Runtime.CompilerServices;
using Npgsql;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// A PostgreSQL account for the game database tests (1.0.37): <c>PULSAR_TEST_POSTGRES_ADMIN</c>, a connection string
/// (without a database) whose role may create databases and roles (CREATEDB CREATEROLE, not a superuser), on the same
/// server as <c>PULSAR_TEST_POSTGRES</c>.
/// </summary>
public static class TestPostgresHost
{
    private const string ConnectionVariable = "PULSAR_TEST_POSTGRES_ADMIN";

    public static bool IsEnabled => TestDatabase.IsEnabled
        && (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable))
            || string.Equals(Environment.GetEnvironmentVariable("PULSAR_REQUIRE_POSTGRES_ADMIN"), "true", StringComparison.OrdinalIgnoreCase));

    public static NpgsqlConnectionStringBuilder Settings =>
        new(Environment.GetEnvironmentVariable(ConnectionVariable) ?? throw new InvalidOperationException($"{ConnectionVariable} is not set."));

    /// <summary>Opens a connection as <paramref name="user"/> (or the test account) to <paramref name="database"/> (or <c>postgres</c>).</summary>
    public static async Task<NpgsqlConnection> OpenAsync(string? user = null, string? password = null, string? database = null)
    {
        var settings = Settings;
        if (user is not null)
        {
            settings.Username = user;
            settings.Password = password;
        }

        settings.Database = database ?? "postgres";
        settings.Pooling = false;
        settings.GssEncryptionMode = GssEncryptionMode.Disable;
        var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>
    /// Asserts that <paramref name="user"/> cannot sign in to <paramref name="database"/>. The server says why (an
    /// authorization or authentication error), except that on Windows it can close the connection before its answer is read,
    /// which Npgsql reports as a broken stream while signing in: both are a refusal.
    /// </summary>
    public static async Task AssertRefusedAsync(string user, string? password, string database)
    {
        var refused = await Assert.ThrowsAnyAsync<NpgsqlException>(() => OpenAsync(user, password, database));
        if (refused is PostgresException answer)
        {
            Assert.True(answer.SqlState.StartsWith("28", StringComparison.Ordinal) || answer.SqlState is "42501" or "3D000",
                $"Unexpected refusal: {answer.SqlState} {answer.MessageText}");
        }
        else
        {
            Assert.IsAssignableFrom<IOException>(refused.InnerException);
        }
    }

    public static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>A test that needs PostgreSQL with an account that may create databases and roles; skipped without it.</summary>
public sealed class PostgresHostFactAttribute : FactAttribute
{
    public PostgresHostFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Set PULSAR_TEST_POSTGRES and PULSAR_TEST_POSTGRES_ADMIN to run the PostgreSQL game database tests (see CONTRIBUTING.md).";
        SkipUnless = nameof(TestPostgresHost.IsEnabled);
        SkipType = typeof(TestPostgresHost);
    }
}
