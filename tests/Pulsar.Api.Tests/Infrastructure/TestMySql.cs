using System.Runtime.CompilerServices;
using MySqlConnector;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// A real MySQL or MariaDB server for the game database tests: PULSAR_TEST_MYSQL is a connection string whose user may
/// create databases and users (CONTRIBUTING.md). Each test's databases carry its server's random short id, so runs
/// never collide; without the variable the tests are skipped.
/// </summary>
public static class TestMySql
{
    private const string ConnectionVariable = "PULSAR_TEST_MYSQL";

    public static bool IsEnabled => TestDatabase.IsEnabled && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable));

    public static MySqlConnectionStringBuilder Settings =>
        new(Environment.GetEnvironmentVariable(ConnectionVariable) ?? throw new InvalidOperationException($"{ConnectionVariable} is not set."));

    /// <summary>Opens a connection as <paramref name="user"/> (or the test account), to a database or none.</summary>
    public static async Task<MySqlConnection> OpenAsync(string? user = null, string? password = null, string? database = null)
    {
        var settings = Settings;
        if (user is not null)
        {
            settings.UserID = user;
            settings.Password = password;
        }

        settings.Database = database ?? string.Empty;
        settings.Pooling = false;
        var connection = new MySqlConnection(settings.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public static async Task<List<string>> ColumnAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}

/// <summary>A test that needs PostgreSQL and MySQL/MariaDB; skipped without both.</summary>
public sealed class GameDatabaseFactAttribute : FactAttribute
{
    public GameDatabaseFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Set PULSAR_TEST_POSTGRES and PULSAR_TEST_MYSQL to run the game database tests (see CONTRIBUTING.md).";
        SkipUnless = nameof(TestMySql.IsEnabled);
        SkipType = typeof(TestMySql);
    }
}
