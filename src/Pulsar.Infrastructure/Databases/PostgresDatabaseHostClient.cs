using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pulsar.Application.Databases;

namespace Pulsar.Infrastructure.Databases;

/// <summary>
/// PostgreSQL through Npgsql (1.0.37). A server's database is owned by a role of the same name, which may log in and
/// nothing else (no superuser, no creating databases or roles, no replication, no row security bypass), with at most
/// <see cref="ConnectionLimit"/> sessions (a game cannot use up the connections the panel's own database needs); the database is
/// made from <c>template0</c> (nothing anyone put in <c>template1</c> comes along) and PUBLIC may not connect to it, so
/// only its role (and the panel's account) can. DDL takes no parameters, so every name is checked against a strict
/// pattern here (whatever the caller checked before) and only then quoted. The password is sent as a SCRAM-SHA-256
/// verifier made here, never in clear: PostgreSQL keeps it as given, and a statement in its log shows no password.
/// Each call opens its own connection to the <c>postgres</c> database (no pool: hosts are few and calls rare) with TLS
/// when the server offers it, or only with TLS when the host requires it, and then with SCRAM bound to the TLS channel
/// (a man in the middle cannot relay the sign-in); the panel's account signs in with SCRAM-SHA-256 only, never sending
/// its password in clear or as MD5.
/// </summary>
/// <remarks>
/// PostgreSQL 16 or newer (<see cref="CheckAsync"/>). The panel's account needs CREATEDB and CREATEROLE (or is a
/// superuser). It grants itself each role it creates: a role with CREATEROLE may do that for the roles it made, and
/// needs it to make one the owner of a database and to drop that database again (with INHERIT and SET).
/// </remarks>
internal sealed partial class PostgresDatabaseHostClient(ILogger<PostgresDatabaseHostClient> logger) : IDatabaseHostClient
{
    /// <summary>Sessions a server's role may hold at once.</summary>
    internal const int ConnectionLimit = 16;

    private const int ScramIterations = 4096;
    private const int MinimumMajorVersion = 16;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Also refuses a PostgreSQL older than 16: there CREATEROLE may change any role that is not a superuser, the
    /// panel's own (when it shares the server) included; from 16 only the roles it was given admin rights on.
    /// </summary>
    public async Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken) =>
        await RunAsync(host, async connection =>
        {
            if (connection.PostgreSqlVersion.Major < MinimumMajorVersion)
            {
                throw new DatabaseHostException(string.Create(CultureInfo.InvariantCulture,
                    $"PostgreSQL {MinimumMajorVersion} or newer is needed (this server is {connection.PostgreSqlVersion.Major})."));
            }

            await using var command = new NpgsqlCommand("SELECT version()", connection);
            return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? "?";
        }, cancellationToken);

    public async Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken)
    {
        var role = Identifier(name);
        var verifier = Secret(password);
        await RunAsync(host, async connection =>
        {
            // Whatever stops a later step (a refusal, the request given up), what this call made goes again, and only
            // that: a role or a database of that name already there is someone's, never adopted. A statement that did not
            // come back (the connection lost) may have been done all the same: what it made may then stay behind (a later
            // creation of that name is refused), and what could not be removed is logged.
            var roleMade = false;
            var databaseMade = false;
            try
            {
                await ExecuteAsync(connection,
                    $"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS " +
                    $"CONNECTION LIMIT {ConnectionLimit.ToString(CultureInfo.InvariantCulture)} PASSWORD {verifier}",
                    cancellationToken);
                roleMade = true;
                await ExecuteAsync(connection, $"GRANT {role} TO CURRENT_USER WITH INHERIT TRUE, SET TRUE", cancellationToken);
                // Keep the host as owner and connections closed until the database's permissions are ready.
                await ExecuteAsync(connection, $"CREATE DATABASE {role} TEMPLATE template0 ALLOW_CONNECTIONS false", cancellationToken);
                databaseMade = true;
                await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {role} FROM PUBLIC", cancellationToken);
                await ExecuteAsync(connection, $"ALTER DATABASE {role} OWNER TO {role}", cancellationToken);
                await ExecuteAsync(connection, $"ALTER DATABASE {role} ALLOW_CONNECTIONS true", cancellationToken);
            }
            catch (Exception ex) when (roleMade)
            {
                try
                {
                    if (databaseMade)
                    {
                        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {role} WITH (FORCE)", CancellationToken.None);
                    }

                    await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {role}", CancellationToken.None);
                }
                catch (Exception undo) when (undo is NpgsqlException or InvalidOperationException)
                {
                    LogLeft(name, host.Host, undo.Message, ex.Message);
                }

                throw;
            }

            return true;
        }, cancellationToken);
    }

    public async Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken)
    {
        var role = Identifier(name);
        await RunAsync(host, async connection =>
        {
            bool exists;
            await using (var query = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = $1", connection))
            {
                query.Parameters.Add(new NpgsqlParameter { Value = name });
                exists = await query.ExecuteScalarAsync(cancellationToken) is not null;
            }

            if (exists)
            {
                await ExecuteAsync(connection, $"GRANT {role} TO CURRENT_USER WITH INHERIT TRUE, SET TRUE", cancellationToken);
                // The role first: nobody can connect while the data goes (WITH (FORCE) ends its sessions). A database that
                // could not go is its role's to use again, as before.
                await ExecuteAsync(connection, $"ALTER ROLE {role} NOLOGIN", cancellationToken);
                try
                {
                    await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {role} WITH (FORCE)", cancellationToken);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    try
                    {
                        await ExecuteAsync(connection, $"ALTER ROLE {role} LOGIN", CancellationToken.None);
                    }
                    catch (Exception restore) when (restore is NpgsqlException or InvalidOperationException)
                    {
                        LogLoginLeftOff(name, host.Host, restore.Message);
                    }

                    throw;
                }
            }
            else
            {
                await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {role} WITH (FORCE)", cancellationToken);
            }

            await ExecuteAsync(connection, $"DROP ROLE IF EXISTS {role}", cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken)
    {
        var role = Identifier(name);
        var verifier = Secret(password);
        await RunAsync(host, async connection =>
        {
            await ExecuteAsync(connection, $"ALTER ROLE {role} PASSWORD {verifier}", cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// What PostgreSQL keeps for a password with <c>password_encryption = scram-sha-256</c> (RFC 5802, 7677):
    /// <c>SCRAM-SHA-256$iterations:salt$StoredKey:ServerKey</c>. The passwords Pulsar makes are letters and digits, which
    /// SASLprep leaves as they are.
    /// </summary>
    internal static string ScramVerifier(string password, byte[] salt, int iterations = ScramIterations)
    {
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var storedKey = SHA256.HashData(HMACSHA256.HashData(salted, "Client Key"u8));
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return string.Create(CultureInfo.InvariantCulture,
            $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
    }

    /// <summary>
    /// How the panel's account connects to <paramref name="host"/>'s <c>postgres</c> database: TLS when offered (required, and
    /// the sign-in bound to it, when the host requires it), SCRAM-SHA-256 only, no pool.
    /// </summary>
    internal static string ConnectionString(DatabaseHostConnection host, TimeSpan commandTimeout) => new NpgsqlConnectionStringBuilder
    {
        Host = host.Host,
        Port = host.Port,
        Username = host.UserName,
        Password = host.Password,
        Database = "postgres",
        // Neither checks the certificate (self-signed ones are the rule); Require at least never falls back to plain text,
        // and binds the sign-in to the TLS channel.
        SslMode = host.RequireTls ? SslMode.Require : SslMode.Prefer,
        ChannelBinding = host.RequireTls ? ChannelBinding.Require : ChannelBinding.Prefer,
        RequireAuth = "ScramSHA256",
        GssEncryptionMode = GssEncryptionMode.Disable,
        Pooling = false,
        Timeout = (int)s_timeout.TotalSeconds,
        CommandTimeout = (int)commandTimeout.TotalSeconds,
        ApplicationName = "Pulsar",
        IncludeErrorDetail = false,
    }.ConnectionString;

    private static async Task<T> RunAsync<T>(DatabaseHostConnection host, Func<NpgsqlConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString(host, s_timeout));
            await connection.OpenAsync(cancellationToken);
            return await work(connection);
        }
        catch (NpgsqlException ex)
        {
            // PostgreSQL's own message ("28P01: password authentication failed for user …", "42501: permission denied to
            // create role"); it never holds the password.
            throw new DatabaseHostException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DatabaseHostException or ArgumentException))
        {
            // Anything else the driver or the network throws: the host could not be worked with all the same.
            throw new DatabaseHostException(ex.Message, ex);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A database (and role) name as <c>ServerDatabase.NameFor</c> makes them, in double quotes.</summary>
    private static string Identifier(string name) =>
        NamePattern().IsMatch(name) ? $"\"{name}\"" : throw new ArgumentException("Not a name Pulsar creates.", nameof(name));

    /// <summary>The password's SCRAM verifier with a new salt, quoted (it holds letters, digits, + / = $ and : only).</summary>
    private static string Secret(string password) =>
        PasswordPattern().IsMatch(password)
            ? $"'{ScramVerifier(password, RandomNumberGenerator.GetBytes(16))}'"
            : throw new ArgumentException("Not a password Pulsar makes.", nameof(password));

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "What was made for the database {Database} on {Host} could not be removed after a failed creation ({Reason}; it failed with: {Failure}): drop it by hand")]
    private partial void LogLeft(string database, string host, string reason, string failure);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The database {Database} on {Host} could not be dropped, and its role could not sign in again ({Reason}): ALTER ROLE it LOGIN by hand")]
    private partial void LogLoginLeftOff(string database, string host, string reason);

    // \z, not $: $ also matches before a final line feed.
    [GeneratedRegex(@"^s[a-z0-9]{1,12}_[a-z0-9_]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{16,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();
}
