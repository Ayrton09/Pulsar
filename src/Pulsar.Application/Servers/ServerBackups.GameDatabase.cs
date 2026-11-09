using Microsoft.Extensions.Logging;
using Pulsar.Application.Databases;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>
/// The game's database in its server's backups (1.0.37; docs/architecture/05, "Backups"): the panel dumps it with
/// PostgreSQL's own programs and hands the dump to the node, which keeps it in the archive; a full restore hands it back,
/// and the panel restores it before the node writes a file. Whole copies (a clone's, a transfer's) never carry it: a
/// transfer leaves the database where it is, and a clone of such a server is refused.
/// </summary>
public sealed partial class ServerBackups
{
    /// <summary>How long a restore's ticket for the dump waits for the node, which first fetches and checks the archive.</summary>
    private static readonly TimeSpan s_databaseClaim = TimeSpan.FromHours(6);

    /// <summary>
    /// The dump of the server's game database for a new backup, handed to its node with a single-use ticket: null when the
    /// game has none. The dump's file goes once the node took it, or could not.
    /// </summary>
    private async Task<Result<BackupDatabaseDump?>> GameDatabaseDumpAsync(Server server, CancellationToken cancellationToken)
    {
        if (server.GameDatabaseId is null)
        {
            return (BackupDatabaseDump?)null;
        }

        // An older agent would make the backup without it: one that looks whole, and is not.
        if (Connection(server.NodeId, AgentCapabilities.GameDatabaseBackups) is { IsSuccess: false })
        {
            return ServerErrors.GameDatabaseBackupsUnsupported;
        }

        var target = await gameDatabases.GameTargetAsync(server, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        if (target.Value is not { } found)
        {
            return (BackupDatabaseDump?)null;
        }

        GameDatabaseDump dump;
        try
        {
            dump = await dumps.DumpAsync(found.Target, cancellationToken);
        }
        catch (GameDatabaseDumpException ex)
        {
            LogDumpFailed(server.Id, ex.Message);
            return ServerErrors.GameDatabaseBackupFailed;
        }

        var content = new FileStream(dump.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var upload = transfers.BeginUpload(server.NodeId, null, content, dump.Size);
        if (upload is null)
        {
            await content.DisposeAsync();
            Delete(dump.Path);
            return ServerErrors.FileTransfersBusy;
        }

        _ = ForgetDumpAsync(upload, content, dump.Path);
        return new BackupDatabaseDump(upload.Ticket, dump.Size, dump.Sha256);
    }

    /// <summary>Once the node took the dump (or the time for it ran out), its place and its file go.</summary>
    private async Task ForgetDumpAsync(FileTransfers.Upload upload, FileStream content, string path)
    {
        try
        {
            await Task.WhenAny(upload.Sent, Task.Delay(FileTransfers.ClaimTimeout + FileTransfers.MaxDuration(upload.Size), clock, upload.Aborted));
        }
        catch (OperationCanceledException)
        {
            // Aborted: nothing more to wait for.
        }
        finally
        {
            upload.Dispose();
            await content.DisposeAsync();
            Delete(path);
        }
    }

    /// <summary>
    /// For a full restore of a backup that holds the game's database, into a server whose game has one: the ticket the
    /// node sends the dump back with, and what restores it once it arrives (started by the caller once the restore runs).
    /// </summary>
    private async Task<Result<(FileTransfers.Download Download, GameDatabaseTarget Target, DatabaseHostConnection Admin)?>> GameDatabaseRestoreAsync(
        Server server, Backup backup, IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        if (!backup.HasGameDatabase || paths is not null || server.GameDatabaseId is null)
        {
            return ((FileTransfers.Download, GameDatabaseTarget, DatabaseHostConnection)?)null;
        }

        if (Connection(server.NodeId, AgentCapabilities.GameDatabaseBackups) is { IsSuccess: false })
        {
            return ServerErrors.GameDatabaseBackupsUnsupported;
        }

        var target = await gameDatabases.GameTargetAsync(server, cancellationToken);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        if (target.Value is not { } found)
        {
            return ((FileTransfers.Download, GameDatabaseTarget, DatabaseHostConnection)?)null;
        }

        return transfers.BeginDownload(server.NodeId, null) is { } download
            ? (download, found.Target, found.Admin)
            : ServerErrors.FileTransfersBusy;
    }

    /// <summary>
    /// Restores the game's database from what the node sends, and answers it: a refusal ends the restore before a file
    /// is written. What PostgreSQL or its programs said stays in the panel's log.
    /// </summary>
    private async Task RestoreGameDatabaseAsync(ServerId server, OperationId operation, FileTransfers.Download download, long expected,
        GameDatabaseTarget target, DatabaseHostConnection admin)
    {
        using (download)
        {
            try
            {
                var (content, length) = await download.Content.WaitAsync(s_databaseClaim, clock, download.Aborted);
                waitingRestores.Remove(operation);
                // Exactly the dump the backup was made with: the panel's disk takes no more.
                if (length != expected)
                {
                    LogDatabaseNotRestored(server, $"the node sent {length} bytes, the backup's dump has {expected}");
                    download.Refuse("The backup's dump of the game's database is not the one it was made with.");
                    return;
                }

                await dumps.RestoreAsync(target, admin, content, length, download.ContentReceived, download.Aborted);
                download.Finish(true);
                LogDatabaseRestored(server, target.Name);
            }
            catch (GameDatabaseDumpException ex)
            {
                LogDatabaseNotRestored(server, ex.Message);
                download.Refuse(ex.Message.Contains("being accessed by other users", StringComparison.Ordinal)
                    ? "Another program is connected to the game's database (a website, say): close it, then restore again."
                    : "The panel could not restore it: an administrator finds why in the panel's log.");
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                LogDatabaseNotRestored(server, ex.Message);
                download.Refuse("The panel did not get the game's database from the backup.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nobody awaits this: whatever else went wrong is logged, and the node told.
                LogDatabaseNotRestored(server, ex.ToString());
                download.Refuse("The panel could not restore it: an administrator finds why in the panel's log.");
            }
            finally
            {
                waitingRestores.Remove(operation);
            }
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The game database of server {ServerId} could not be dumped for a backup: {Reason}")]
    private partial void LogDumpFailed(ServerId serverId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored the game database {Database} of server {ServerId} from a backup")]
    private partial void LogDatabaseRestored(ServerId serverId, string database);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The game database of server {ServerId} was not restored from a backup: {Reason}")]
    private partial void LogDatabaseNotRestored(ServerId serverId, string reason);
}
