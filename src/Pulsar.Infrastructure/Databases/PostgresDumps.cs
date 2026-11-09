using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pulsar.Application.Databases;

namespace Pulsar.Infrastructure.Databases;

/// <summary>How the panel dumps and restores games' databases (<c>GameDatabases</c> in its settings).</summary>
public sealed class GameDatabaseOptions
{
    public const string SectionName = "GameDatabases";

    /// <summary>
    /// The folder of <c>pg_dump</c> and <c>pg_restore</c>; empty: the newest PostgreSQL in the usual place
    /// (<c>%ProgramFiles%\PostgreSQL\N\bin</c>, <c>/usr/lib/postgresql/N/bin</c>), else the PATH.
    /// </summary>
    public string? PostgresBin { get; set; }

    /// <summary>The longest a dump or a restore may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Where dumps wait for their node, or for a restore: empty, <c>game-dumps</c> beside the panel's key ring. Made for
    /// the panel's account alone.
    /// </summary>
    public string? DumpDirectory { get; set; }
}

/// <summary>
/// A game's PostgreSQL database dumped and restored with PostgreSQL's own <c>pg_dump</c> and <c>pg_restore</c>, run on the
/// panel's machine as the game's role, never the host's account (1.0.37). The programs get the password in their
/// environment (never on their command line), with every inherited <c>PG*</c> setting dropped first, and are given their
/// arguments one by one (no shell). A dump is PostgreSQL's custom format, without owners or grants; it lives in a folder
/// only the panel's account can open, until its caller deletes it.
/// </summary>
/// <remarks>
/// A restore never touches the database until it is whole: the dump goes into a new database (<c>name_r</c>, made by
/// the host's account for the game's role, closed to PUBLIC) in one transaction that stops at the first error, then the
/// two swap names and the old one is dropped. Another program connected to the old database (a website's role) stops the
/// swap; the game's own sessions are ended first.
/// </remarks>
internal sealed partial class PostgresDumps(IOptions<GameDatabaseOptions> options, ILogger<PostgresDumps> logger) : IGameDatabaseDumps
{
    private const int ErrorTailLength = 600;

    /// <summary>What a dump leaves free on the panel's drive at least (its own database and logs live there too).</summary>
    private const long FreeFloorBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Dumps a crash left behind go once they are this old.</summary>
    private static readonly TimeSpan s_leftOver = TimeSpan.FromHours(6);

    /// <summary>What a program of PostgreSQL's gets of the panel's environment: what Windows and libpq need, nothing more.</summary>
    private static readonly string[] s_keptEnvironment = ["SystemRoot", "windir", "SystemDrive", "TEMP", "TMP", "PATH", "HOME", "LANG", "LC_ALL", "TZ"];
    private static readonly TimeSpan s_statementTimeout = TimeSpan.FromSeconds(30);

    public async Task<GameDatabaseDump> DumpAsync(GameDatabaseTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = Checked(target.Name);
        var folder = DumpFolder();
        EnsureRoom(folder, 0);
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}.dump");
        try
        {
            await RunAsync("pg_dump", target, ["--format=custom", "--no-owner", "--no-privileges", "--no-password", $"--file={file}",
                $"--dbname={name}"], cancellationToken);
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                throw new GameDatabaseDumpException("pg_dump wrote no dump.");
            }

            string sha256;
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            }

            return new GameDatabaseDump(file, info.Length, sha256);
        }
        catch
        {
            TryDelete(file);
            throw;
        }
    }

    public async Task RestoreAsync(GameDatabaseTarget target, DatabaseHostConnection admin, Stream content, long length, Action received,
        CancellationToken receiving)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(received);
        if (length is < 0 or > Pulsar.Contracts.BackupDatabaseDump.MaxSizeBytes)
        {
            throw new GameDatabaseDumpException("The dump is larger than a backup takes.");
        }

        var folder = DumpFolder();
        EnsureRoom(folder, length);
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}.dump");
        var cancellationToken = receiving;
        try
        {
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while (copied < length && (read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                }

                if (copied != length)
                {
                    throw new GameDatabaseDumpException($"The dump ended after {copied} of {length} bytes.");
                }
            }

            // Received: the transfer's own time limit ends here; the restore has the configured one.
            received();
            await RestoreFileAsync(target, admin, file, CancellationToken.None);
        }
        finally
        {
            TryDelete(file);
        }
    }

    private async Task RestoreFileAsync(GameDatabaseTarget target, DatabaseHostConnection admin, string dump, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(admin);
        var name = Checked(target.Name);
        var role = Quote(name);
        // A hyphen, which no database a user names can hold: these never take another database's name.
        var restoring = Quote(name + "-r");
        var old = Quote(name + "-o");
        await AdminAsync(admin, async connection =>
        {
            // A swap cut off between its two renames left the data under the old name: it gets its own name back first.
            if (!await ExistsAsync(connection, name, cancellationToken) && await ExistsAsync(connection, name + "-o", cancellationToken))
            {
                await ExecuteAsync(connection, $"ALTER DATABASE {old} RENAME TO {role}", cancellationToken);
            }

            // Left over by a restore that did not end: never the database itself.
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {restoring} WITH (FORCE)", cancellationToken);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {old} WITH (FORCE)", cancellationToken);
            await ExecuteAsync(connection, $"GRANT {role} TO CURRENT_USER WITH INHERIT TRUE, SET TRUE", cancellationToken);
            await ExecuteAsync(connection, $"CREATE DATABASE {restoring} OWNER {role} TEMPLATE template0", cancellationToken);
            await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {restoring} FROM PUBLIC", cancellationToken);
        }, cancellationToken);

        try
        {
            await RunAsync("pg_restore", target, ["--no-owner", "--no-privileges", "--single-transaction", "--exit-on-error", "--no-password",
                $"--dbname={name}-r", dump], cancellationToken);
            await AdminAsync(admin, async connection =>
            {
                // The game is stopped; whatever of its role still holds the database goes. Anyone else's session stops the swap.
                await using (var end = new NpgsqlCommand(
                                 "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND usename = $1 AND pid <> pg_backend_pid()",
                                 connection))
                {
                    end.Parameters.Add(new NpgsqlParameter { Value = name });
                    await end.ExecuteNonQueryAsync(cancellationToken);
                }

                await ExecuteAsync(connection, $"ALTER DATABASE {role} RENAME TO {old}", CancellationToken.None);
                try
                {
                    await ExecuteAsync(connection, $"ALTER DATABASE {restoring} RENAME TO {role}", CancellationToken.None);
                }
                catch (Exception swap) when (swap is NpgsqlException or InvalidOperationException)
                {
                    // The old data takes its name back, on a new connection if this one is gone; failing that, the next
                    // restore gives it back before anything else (above), and never drops it.
                    try
                    {
                        await ExecuteAsync(connection, $"ALTER DATABASE {old} RENAME TO {role}", CancellationToken.None);
                    }
                    catch (Exception again) when (again is NpgsqlException or InvalidOperationException)
                    {
                        await AdminAsync(admin, other => ExecuteAsync(other, $"ALTER DATABASE {old} RENAME TO {role}", CancellationToken.None),
                            CancellationToken.None);
                    }

                    throw;
                }
            }, cancellationToken);
        }
        catch
        {
            await DropQuietlyAsync(admin, restoring);
            throw;
        }

        // The old data: what cannot go now is a leftover the next restore removes.
        await DropQuietlyAsync(admin, old);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, string database, CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection);
        query.Parameters.Add(new NpgsqlParameter { Value = database });
        return await query.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>The panel's drive keeps <see cref="FreeFloorBytes"/> free after <paramref name="bytes"/> more.</summary>
    private static void EnsureRoom(string folder, long bytes)
    {
        var free = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
        if (free - bytes < FreeFloorBytes)
        {
            throw new GameDatabaseDumpException(string.Create(CultureInfo.InvariantCulture,
                $"The panel's drive has {free / (1024 * 1024)} MB free: a dump needs {bytes / (1024 * 1024)} MB and leaves 2 GB."));
        }
    }

    /// <summary>Runs <paramref name="program"/> against <paramref name="target"/> as its role, within the configured time.</summary>
    private async Task RunAsync(string program, GameDatabaseTarget target, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Tool(program))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add($"--host={target.Host}");
        start.ArgumentList.Add($"--port={target.Port.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--username={target.Name}");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Only what the system needs and this call says: none of the panel's own settings (its secrets among them), and no
        // service file, password file or other server for libpq.
        var kept = s_keptEnvironment.Select(n => (Name: n, Value: Environment.GetEnvironmentVariable(n))).Where(v => v.Value is not null).ToList();
        start.Environment.Clear();
        foreach (var (variable, value) in kept)
        {
            start.Environment[variable] = value;
        }

        start.Environment["PGPASSWORD"] = target.Password;
        start.Environment["PGSSLMODE"] = target.RequireTls ? "require" : "prefer";
        start.Environment["PGCHANNELBINDING"] = target.RequireTls ? "require" : "prefer";
        start.Environment["PGREQUIREAUTH"] = "scram-sha-256";
        start.Environment["PGGSSENCMODE"] = "disable";
        start.Environment["PGCONNECT_TIMEOUT"] = "15";
        start.Environment["PGAPPNAME"] = "Pulsar";
        start.Environment["PGPASSFILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.Timeout);
        using var process = Process.Start(start) ?? throw new GameDatabaseDumpException($"{program} could not be started.");
        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(errors, output);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new GameDatabaseDumpException($"{program} took longer than {options.Value.Timeout}.");
        }

        if (process.ExitCode != 0)
        {
            // libpq's and the program's own words: they name the database and the server, never the password.
            var said = (await errors).Trim();
            if (said.Length > ErrorTailLength)
            {
                said = said[^ErrorTailLength..];
            }

            throw new GameDatabaseDumpException($"{program} failed ({process.ExitCode.ToString(CultureInfo.InvariantCulture)}): {said}");
        }
    }

    /// <summary>The program in the configured folder, the newest PostgreSQL's, or the PATH's.</summary>
    private string Tool(string program)
    {
        var file = OperatingSystem.IsWindows() ? program + ".exe" : program;
        if (options.Value.PostgresBin is { Length: > 0 } configured)
        {
            var path = Path.Combine(configured, file);
            return File.Exists(path) ? path : throw new GameDatabaseDumpException($"{file} is not in {configured} (GameDatabases:PostgresBin).");
        }

        var roots = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL")]
            : new[] { "/usr/lib/postgresql" };
        foreach (var root in roots.Where(Directory.Exists))
        {
            var newest = Directory.EnumerateDirectories(root)
                .Select(d => (Path: d, Version: int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1))
                .Where(d => d.Version > 0 && File.Exists(Path.Combine(d.Path, "bin", file)))
                .OrderByDescending(d => d.Version)
                .FirstOrDefault();
            if (newest.Path is not null)
            {
                return Path.Combine(newest.Path, "bin", file);
            }
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(folder, file);
            if (Path.IsPathFullyQualified(path) && File.Exists(path))
            {
                return path;
            }
        }

        throw new GameDatabaseDumpException(
            $"{file} was not found on the panel's machine: install PostgreSQL's client programs, or name their folder in GameDatabases:PostgresBin.");
    }

    /// <summary>
    /// The folder of dumps, only the panel's account's (made so, or made so again, at every use); what a crash left there
    /// goes once old enough.
    /// </summary>
    private string DumpFolder()
    {
        var folder = Path.GetFullPath(options.Value.DumpDirectory ?? Path.Combine(Path.GetTempPath(), "pulsar-dumps"));
        DataProtectionSetup.CreatePrivate(new DirectoryInfo(folder));
        foreach (var stale in new DirectoryInfo(folder).EnumerateFiles("*.dump").Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > s_leftOver))
        {
            TryDelete(stale.FullName);
        }

        return folder;
    }

    private async Task DropQuietlyAsync(DatabaseHostConnection admin, string database)
    {
        try
        {
            await AdminAsync(admin, connection => ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)", CancellationToken.None),
                CancellationToken.None);
        }
        catch (GameDatabaseDumpException ex)
        {
            LogLeftOver(database, admin.Host, ex.Message);
        }
    }

    private static async Task AdminAsync(DatabaseHostConnection admin, Func<NpgsqlConnection, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseHostClient.ConnectionString(admin, s_statementTimeout));
            await connection.OpenAsync(cancellationToken);
            await work(connection);
        }
        catch (NpgsqlException ex)
        {
            throw new GameDatabaseDumpException(ex.Message, ex);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Checked(string name) =>
        NamePattern().IsMatch(name) ? name : throw new ArgumentException("Not a name Pulsar creates.", nameof(name));

    private static string Quote(string name) => $"\"{name}\"";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database {Database} on {Host} was left behind by a restore ({Reason}): the next restore removes it")]
    private partial void LogLeftOver(string database, string host, string reason);

    // \z, not $: $ also matches before a final line feed. The suffixes a restore adds keep within PostgreSQL's 63.
    [GeneratedRegex(@"^s[a-z0-9]{1,12}_[a-z0-9_]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
