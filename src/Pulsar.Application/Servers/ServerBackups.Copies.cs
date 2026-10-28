using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

public sealed partial class ServerBackups
{
    /// <summary>
    /// Restores <paramref name="sourceBackupId"/>, a backup of another server, onto the stopped <paramref name="targetId"/>
    /// (a clone, Pulsar 1.0.16), cleanly: the source's node sends its archive to the panel, which hands it to the target's
    /// node as it arrives (never storing it), and that node checks it is exactly that archive before it restores it; it
    /// keeps it as a backup of the target. Started as the panel (the caller is a sequence); the restore's operation follows.
    /// </summary>
    /// <param name="recorded">Called with the restore's operation id just before it is first saved: what follows it is saved with it.</param>
    public async Task<Result<Operation>> RestoreFromAsync(ServerId targetId, ServerId sourceId, BackupId sourceBackupId, OperationRequester via,
        CancellationToken cancellationToken, Action<OperationId>? recorded = null)
    {
        var target = await db.Servers.SingleOrDefaultAsync(s => s.Id == targetId, cancellationToken);
        var source = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        var backup = await db.Backups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == sourceBackupId && b.ServerId == sourceId, cancellationToken);
        if (target is null || source is null || backup is null)
        {
            return target is null || source is null ? ServerErrors.NotFound : ServerErrors.BackupNotFound;
        }

        if (backup is not { Status: BackupStatus.Completed, Sha256: { } sha256, SizeBytes: { } size })
        {
            return ServerErrors.BackupNotCompleted;
        }

        if (Connection(source.NodeId, AgentCapabilities.SendBackup) is { IsSuccess: false } sending)
        {
            return sending.Error!;
        }

        if (Connection(target.NodeId, AgentCapabilities.ReceiveBackup) is { IsSuccess: false })
        {
            return ServerErrors.CopyUnsupported;
        }

        if (transfers.BeginDownload(source.NodeId, null) is not { } download)
        {
            return ServerErrors.FileTransfersBusy;
        }

        FileTransfers.Upload? upload = null;
        try
        {
            var remote = await RemoteOfAsync(backup, cancellationToken);
            var sent = Ack(await connections.SendAsync(source.NodeId, new SendBackupCommand(source.Id.Value, backup.Id.Value, download.Ticket, remote),
                null, cancellationToken));
            if (!sent.IsSuccess || sent.Value.Result is not FileEntryResult)
            {
                download.Dispose();
                return sent.IsSuccess ? ServerErrors.CommandRejected : sent.Error!;
            }

            await download.WaitForClaimAsync(cancellationToken);
            var (content, length) = await download.WaitForContentAsync(cancellationToken);
            if (length != size)
            {
                download.Finish(false);
                download.Dispose();
                return ServerErrors.BackupCorrupt;
            }

            upload = transfers.BeginUpload(target.NodeId, null, content, length);
            if (upload is null)
            {
                download.Finish(false);
                download.Dispose();
                return ServerErrors.FileTransfersBusy;
            }

            var now = clock.GetUtcNow();
            var operation = Operation.Create(OperationType.BackupRestore, target.NodeId, target.Id, null, via, now);
            var copy = Backup.Receiving(target, backup, operation, now);
            var run = await operations.RunAsync(target, operation,
                new RestoreBackupCommand(target.Id.Value, copy.Id.Value, Convert.ToHexStringLower(sha256), null, Clean: true, size, upload.Ticket),
                () =>
                {
                    db.Backups.Add(copy);
                    recorded?.Invoke(operation.Id);
                }, cancellationToken);
            if (!run.IsSuccess || !run.Value.IsActive)
            {
                // Not taken by the node (or not even recorded): nothing will fetch the archive.
                if (run.IsSuccess)
                {
                    copy.Received(false, run.Value.ErrorMessage ?? run.Value.ErrorCode, clock.GetUtcNow());
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                download.Finish(false);
                upload.Dispose();
                download.Dispose();
                return run.IsSuccess ? run.Value : run.Error!;
            }

            // The two nodes' requests are joined from here on, for as long as the target's node reads the archive.
            _ = RelayAsync(download, upload, source.Id, target.Id);
            return run.Value;
        }
        catch (Exception ex) when (ex is TimeoutException
            || (ex is OperationCanceledException && download.Aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
        {
            upload?.Dispose();
            download.Dispose();
            return ServerErrors.FileTransferFailed;
        }
        catch
        {
            upload?.Dispose();
            download.Dispose();
            throw;
        }
    }

    /// <summary>Keeps the source node's request open until the target's node has read all of it (or the transfer ended otherwise).</summary>
    private async Task RelayAsync(FileTransfers.Download download, FileTransfers.Upload upload, ServerId source, ServerId target)
    {
        var complete = false;
        try
        {
            // Claimed first, as an upload of the file manager is: only then does its idle clock run.
            await upload.WaitForClaimAsync(CancellationToken.None);
            await upload.WaitUntilSentAsync(CancellationToken.None);
            complete = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRelayFailed(source, target, ex.Message);
        }
        finally
        {
            download.Finish(complete);
            upload.Dispose();
            download.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backup of server {Source} relayed to server {Target} did not all arrive: {Reason}")]
    private partial void LogRelayFailed(ServerId source, ServerId target, string reason);
}
