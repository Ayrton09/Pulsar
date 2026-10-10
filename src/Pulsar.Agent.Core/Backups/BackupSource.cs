using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Backups;

/// <summary>
/// Where a backup reads a server's files (docs/architecture/05, "Backups"). A game that is not running writes nothing:
/// its directory is read as it is. A running one is first asked to save (the manifest's <c>backup.save</c>), then its
/// volume is snapshotted and the snapshot is read; when no snapshot can be taken, the directory is read live and the
/// capture says why. Disposing the source resumes what the save paused, if the snapshot did not let it resume already,
/// and deletes the snapshot.
/// </summary>
public sealed partial class BackupSource : IAsyncDisposable
{
    private readonly ServerSupervisor _server;
    private readonly ServerStore _store;
    private readonly ILogger _logger;
    private IReadOnlyList<string>? _resume;
    private IVolumeSnapshot? _snapshot;

    private BackupSource(ServerSupervisor server, ServerStore store, ProvisionedServer files, ILogger logger)
    {
        (_server, _store, _logger) = (server, store, logger);
        Files = files;
        Capture = new BackupCapture(BackupCaptureMode.Stopped);
    }

    /// <summary>The server as the backup reads it: its directory, or the same directory in the snapshot.</summary>
    public ProvisionedServer Files { get; private set; }

    public BackupCapture Capture { get; private set; }

    /// <param name="snapshots">Null when the agent's administrator turned snapshots off.</param>
    public static async Task<BackupSource> OpenAsync(ServerSupervisor server, ProvisionedServer files, GameSaveDefinition? save,
        IVolumeSnapshots? snapshots, ServerStore store, BackupProgress progress, TimeProvider clock, ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(progress);
        var source = new BackupSource(server, store, files, logger);
        var state = server.Snapshot.State;
        if (state is not (RuntimeState.Starting or RuntimeState.Running or RuntimeState.Stopping))
        {
            return source;
        }

        try
        {
            GameSaveOutcome? saved = null;
            if (state == RuntimeState.Running && save is not null)
            {
                await progress(BackupStages.Saving, null);
                if (save.Resume is { Count: > 0 } resume)
                {
                    // Noted first: if the agent stops before resuming, its next run does it (a game left not saving loses its world).
                    store.SavePausedSave(new PausedSave(server.ServerId, resume));
                    source._resume = resume;
                }

                saved = await GameSave.SaveAsync(server, save, clock, cancellationToken);
                LogSaved(logger, server.ServerId, saved.Value);
            }

            string? error = BackupSnapshotErrors.Disabled;
            if (snapshots is not null)
            {
                await progress(BackupStages.Snapshotting, null);
                try
                {
                    source._snapshot = await snapshots.TakeAsync(files.Directory, cancellationToken);
                    error = null;
                }
                catch (SnapshotException ex)
                {
                    error = ex.Code;
                    LogNoSnapshot(logger, server.ServerId, ex.Code, ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A snapshot is an improvement: whatever goes wrong taking one, the backup is still made, live.
                    error = BackupSnapshotErrors.Failed;
                    LogSnapshotBroke(logger, ex, server.ServerId);
                }
            }

            if (source._snapshot is { } snapshot)
            {
                source.Files = files with { Directory = snapshot.Directory };
                // The snapshot holds what the game saved: it may save again while the backup reads the snapshot.
                await source.ResumeAsync();
            }

            source.Capture = new BackupCapture(source._snapshot is null ? BackupCaptureMode.Live : BackupCaptureMode.Snapshot, error, saved);
            return source;
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Resumes the games a backup paused before an earlier run of the agent ended (their records in the state directory);
    /// a game that is no longer running starts saving again by itself.
    /// </summary>
    public static async Task ResumePausedAsync(ServerStore store, Func<Guid, ServerSupervisor?> servers, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(servers);
        foreach (var paused in store.LoadPausedSaves())
        {
            if (servers(paused.ServerId) is { Snapshot.State: RuntimeState.Running } server)
            {
                await GameSave.ResumeAsync(server, paused.Resume);
                LogResumedAfterRestart(logger, paused.ServerId);
            }

            Forget(store, paused.ServerId, logger);
        }
    }

    /// <summary>Sends what undoes the save's pause, once.</summary>
    public async Task ResumeAsync()
    {
        if (_resume is not { } resume)
        {
            return;
        }

        _resume = null;
        await GameSave.ResumeAsync(_server, resume);
        Forget(_store, _server.ServerId, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ResumeAsync();
        }
        finally
        {
            if (_snapshot is { } snapshot)
            {
                _snapshot = null;
                await snapshot.DisposeAsync();
            }
        }
    }

    private static void Forget(ServerStore store, Guid serverId, ILogger logger)
    {
        try
        {
            store.DeletePausedSave(serverId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotForgotten(logger, serverId, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} was asked to save before a backup: {Outcome}")]
    private static partial void LogSaved(ILogger logger, Guid serverId, GameSaveOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup of server {ServerId} reads the running game's files live, without a snapshot ({Code}): {Reason}")]
    private static partial void LogNoSnapshot(ILogger logger, Guid serverId, string code, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup of server {ServerId} reads the running game's files live: taking a snapshot failed unexpectedly")]
    private static partial void LogSnapshotBroke(ILogger logger, Exception exception, Guid serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId} was told to resume saving: a backup had paused it before the agent stopped")]
    private static partial void LogResumedAfterRestart(ILogger logger, Guid serverId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The paused save of server {ServerId} could not be forgotten: {Reason}")]
    private static partial void LogNotForgotten(ILogger logger, Guid serverId, string reason);
}
