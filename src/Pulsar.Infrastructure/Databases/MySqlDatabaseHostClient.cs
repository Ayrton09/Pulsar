using System.Text.RegularExpressions;
using MySqlConnector;
using Pulsar.Application.Databases;

namespace Pulsar.Infrastructure.Databases;

/// <summary>
/// MySQL/MariaDB through MySqlConnector. DDL takes no parameters, so every name, user host and password is checked
/// against a strict pattern here (whatever the caller checked before) and only then quoted into the statement. Each
/// call opens its own connection (no pool: hosts are few and calls rare) with TLS when the server offers it, or only
/// with TLS when the host requires it.
/// </summary>
internal sealed partial class MySqlDatabaseHostClient : IDatabaseHostClient
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);

    public async Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken) =>
        await RunAsync(host, async connection =>
        {
            await using var command = new MySqlCommand("SELECT VERSION()", connection);
            return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        }, cancellationToken);

    public async Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken)
    {
        var database = Identifier(name);
        var user = Account(name, userHost);
        var secret = Secret(password);
        // "_" is a wildcard in a grant's database (as is "%", which names never hold): escaped, the user gets this
        // database alone, not look-alikes it could then create (s…_main would also match s…1main).
        var grantDatabase = database.Replace("_", @"\_", StringComparison.Ordinal);
        await RunAsync(host, async connection =>
        {
            // No IF NOT EXISTS: a database already there is someone's data, never adopted.
            await ExecuteAsync(connection, $"CREATE DATABASE {database}", cancellationToken);
            // Whatever stops a later step (a refusal, the request given up), what was made goes again: the user too when
            // CREATE USER itself was interrupted (the server may have made it all the same), unless it was there before.
            var someoneElses = false;
            try
            {
                try
                {
                    await ExecuteAsync(connection, $"CREATE USER {user} IDENTIFIED BY {secret}", cancellationToken);
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.CannotUser)
                {
                    someoneElses = true;
                    throw;
                }

                await ExecuteAsync(connection, $"GRANT ALL PRIVILEGES ON {grantDatabase}.* TO {user}", cancellationToken);
            }
            catch
            {
                await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database}", CancellationToken.None);
                if (!someoneElses)
                {
                    await ExecuteAsync(connection, $"DROP USER IF EXISTS {user}", CancellationToken.None);
                }

                throw;
            }

            return true;
        }, cancellationToken);
    }

    public async Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken)
    {
        var database = Identifier(name);
        var user = Account(name, userHost);
        await RunAsync(host, async connection =>
        {
            // The user first: nobody can connect while the data goes.
            await ExecuteAsync(connection, $"DROP USER IF EXISTS {user}", cancellationToken);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database}", cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken)
    {
        var user = Account(name, userHost);
        var secret = Secret(password);
        await RunAsync(host, async connection =>
        {
            await ExecuteAsync(connection, $"ALTER USER {user} IDENTIFIED BY {secret}", cancellationToken);
            return true;
        }, cancellationToken);
    }

    private static async Task<T> RunAsync<T>(DatabaseHostConnection host, Func<MySqlConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        var settings = new MySqlConnectionStringBuilder
        {
            Server = host.Host,
            Port = (uint)host.Port,
            UserID = host.UserName,
            Password = host.Password,
            // Neither checks the certificate (self-signed ones are the rule); Required at least never falls back to plain text.
            SslMode = host.RequireTls ? MySqlSslMode.Required : MySqlSslMode.Preferred,
            Pooling = false,
            ConnectionTimeout = (uint)s_timeout.TotalSeconds,
            DefaultCommandTimeout = (uint)s_timeout.TotalSeconds,
            AllowUserVariables = false,
            AllowLoadLocalInfile = false,
        };
        try
        {
            await using var connection = new MySqlConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return await work(connection);
        }
        catch (MySqlException ex)
        {
            // MySQL's own message ("Access denied for user …", "Unable to connect to any of the specified MySQL hosts.");
            // it never holds the password.
            throw new DatabaseHostException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DatabaseHostException))
        {
            // Anything else the connector or the network throws: the host could not be worked with all the same.
            throw new DatabaseHostException(ex.Message, ex);
        }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A database (and user) name as <c>ServerDatabase.NameFor</c> makes them, in backquotes.</summary>
    private static string Identifier(string name) =>
        NamePattern().IsMatch(name) ? $"`{name}`" : throw new ArgumentException("Not a name Pulsar creates.", nameof(name));

    /// <summary><c>'name'@'host'</c>.</summary>
    private static string Account(string name, string userHost) =>
        NamePattern().IsMatch(name) && UserHostPattern().IsMatch(userHost)
            ? $"'{name}'@'{userHost}'"
            : throw new ArgumentException("Not an account Pulsar creates.", nameof(name));

    private static string Secret(string password) =>
        PasswordPattern().IsMatch(password) ? $"'{password}'" : throw new ArgumentException("Not a password Pulsar makes.", nameof(password));

    // \z, not $: $ also matches before a final line feed.
    [GeneratedRegex(@"^s[a-z0-9]{1,12}_[a-z0-9_]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9.:%_-]{1,255}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UserHostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{16,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();
}
