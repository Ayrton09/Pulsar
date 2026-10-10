using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Backups;
using Pulsar.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.Storage.Vss;

namespace Pulsar.Agent.Windows.Backups;

/// <summary>
/// Snapshots through the Volume Shadow Copy service (docs/architecture/06, "Backup snapshots"): a requester that takes a
/// non-persistent, auto-release shadow copy of one volume without writers (<c>VSS_CTX_FILE_SHARE_BACKUP</c>; games are
/// not VSS writers, their own save is asked for through the console first). Windows deletes such a snapshot when its
/// requester lets it go, or when the agent's process ends, so none outlives the backup that took it. Taking one needs
/// administrator rights: a development agent running as its user gets <see cref="BackupSnapshotErrors.NotPermitted"/>.
/// </summary>
public sealed partial class WindowsVolumeSnapshots(ILogger<WindowsVolumeSnapshots> logger) : IVolumeSnapshots
{
    private const uint FileReadAttributes = 0x0080;

    // One snapshot set is created at a time on a machine: the agent's own backups wait for each other, and another
    // program's set (VSS_E_SNAPSHOT_SET_IN_PROGRESS) is waited for a while.
    private static readonly SemaphoreSlim s_creating = new(1, 1);
    private static readonly TimeSpan s_busyRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_busyLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(100);

    public async Task<IVolumeSnapshot> TakeAsync(string directory, CancellationToken cancellationToken)
    {
        var (volume, relative) = Locate(directory);
        await s_creating.WaitAsync(cancellationToken);
        try
        {
            var waited = TimeSpan.Zero;
            while (true)
            {
                try
                {
                    var taken = await Run(() => Create(volume, cancellationToken));
                    LogTaken(directory, taken.Device);
                    return new Snapshot(taken, Path.Join(taken.Device, relative), logger);
                }
                catch (VssFailure ex) when (ex.HResult == HRESULT.VSS_E_SNAPSHOT_SET_IN_PROGRESS && waited < s_busyLimit)
                {
                    await Task.Delay(s_busyRetry, cancellationToken);
                    waited += s_busyRetry;
                }
            }
        }
        catch (VssFailure ex)
        {
            throw new SnapshotException(Reason(ex.HResult), $"Windows could not take a snapshot of {volume}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SnapshotException(BackupSnapshotErrors.Unsupported, $"This Windows has no Volume Shadow Copy service: {ex.Message}", ex);
        }
        finally
        {
            s_creating.Release();
        }
    }

    /// <summary>What a failure means for the user (<see cref="BackupSnapshotErrors"/>).</summary>
    internal static string Reason(int hresult) => hresult switch
    {
        _ when hresult == HRESULT.E_ACCESSDENIED => BackupSnapshotErrors.NotPermitted,
        _ when hresult == HRESULT.VSS_E_VOLUME_NOT_SUPPORTED || hresult == HRESULT.VSS_E_VOLUME_NOT_SUPPORTED_BY_PROVIDER =>
            BackupSnapshotErrors.Unsupported,
        _ when hresult == HRESULT.VSS_E_INSUFFICIENT_STORAGE || hresult == HRESULT.VSS_E_MAXIMUM_NUMBER_OF_SNAPSHOTS_REACHED
            || hresult == HRESULT.VSS_E_MAXIMUM_DIFFAREA_ASSOCIATIONS_REACHED => BackupSnapshotErrors.NoSpace,
        _ when hresult == HRESULT.VSS_E_SNAPSHOT_SET_IN_PROGRESS => BackupSnapshotErrors.Busy,
        // The service is disabled (ERROR_SERVICE_DISABLED) or not answering (RPC_S_SERVER_UNAVAILABLE, a stopped dependency).
        unchecked((int)0x80070422) or unchecked((int)0x800706BA) or unchecked((int)0x8007042C) => BackupSnapshotErrors.Unavailable,
        _ => BackupSnapshotErrors.Failed,
    };

    /// <summary>
    /// The volume the directory is on (<c>\\?\Volume{guid}\</c>, after any junction to it, as the file manager follows
    /// one) and the directory's path on it.
    /// </summary>
    internal static unsafe (string Volume, string Relative) Locate(string directory)
    {
        using var handle = PInvoke.CreateFile(directory, FileReadAttributes,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (handle.IsInvalid)
        {
            throw new SnapshotException(BackupSnapshotErrors.Failed, $"Cannot open {directory}: error {Marshal.GetLastPInvokeError()}.");
        }

        var buffer = new char[32 * 1024];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = PInvoke.GetFinalPathNameByHandle((HANDLE)handle.DangerousGetHandle(), pointer, (uint)buffer.Length,
                GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_GUID);
        }

        // A network share or a RAM disk has no volume name: no shadow copy either.
        if (length == 0 || length >= buffer.Length || VolumePath().Match(new string(buffer, 0, (int)length)) is not { Success: true } match)
        {
            throw new SnapshotException(BackupSnapshotErrors.Unsupported, $"{directory} is not on a local volume that can have snapshots.");
        }

        return (match.Groups["volume"].Value, match.Groups["relative"].Value);
    }

    /// <summary>
    /// Takes the snapshot; on failure the requester is released, which removes anything it had taken. Without writers
    /// there is no backup components document: no <c>GatherWriterMetadata</c> and no <c>PrepareForBackup</c> (which
    /// answers <c>VSS_E_BAD_STATE</c> then, as found in Windows Sandbox; Microsoft's vshadow skips both too).
    /// </summary>
    private static unsafe Taken Create(string volume, CancellationToken cancellationToken)
    {
        var step = "CreateVssBackupComponents";
        IVssBackupComponents? backup = null;
        try
        {
            PInvoke.CreateVssBackupComponentsInternal(out backup).ThrowOnFailure();
            step = "InitializeForBackup";
            backup.InitializeForBackup(default);
            step = "SetContext";
            backup.SetContext((int)VSS_SNAPSHOT_CONTEXT.VSS_CTX_FILE_SHARE_BACKUP);
            // A copy backup: other backup programs' incremental history is left alone.
            step = "SetBackupState";
            backup.SetBackupState(0, 0, VSS_BACKUP_TYPE.VSS_BT_COPY, 0);
            step = "StartSnapshotSet";
            backup.StartSnapshotSet(out _);
            Guid snapshotId;
            step = "AddToSnapshotSet";
            fixed (char* name = volume)
            {
                backup.AddToSnapshotSet(in *(ushort*)name, Guid.Empty, out snapshotId);
            }

            step = "DoSnapshotSet";
            backup.DoSnapshotSet(out var taking);
            Await(taking, cancellationToken);
            step = "GetSnapshotProperties";
            backup.GetSnapshotProperties(snapshotId, out var properties);
            try
            {
                return new Taken(backup, snapshotId, new string((char*)properties.m_pwszSnapshotDeviceObject));
            }
            finally
            {
                PInvoke.VssFreeSnapshotPropertiesInternal(in properties);
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DllNotFoundException or EntryPointNotFoundException))
        {
            Release(backup);
            // Which step failed tells more than the code alone (the agent logs it). A Windows without the service's
            // library is not a failed step: TakeAsync says it has no snapshots.
            throw new VssFailure(step, ex);
        }
        catch
        {
            Release(backup);
            throw;
        }
    }

    private static void Release(IVssBackupComponents? backup)
    {
        if (backup is not null)
        {
            Marshal.FinalReleaseComObject(backup);
        }
    }

    /// <summary>Waits for an asynchronous VSS step, cancelling it when asked; its failure is thrown as a <see cref="COMException"/>.</summary>
    private static unsafe void Await(IVssAsync pending, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                HRESULT status;
                var reserved = 0;
                pending.QueryStatus(&status, ref reserved);
                if (status != HRESULT.VSS_S_ASYNC_PENDING)
                {
                    if (status.Failed)
                    {
                        Marshal.ThrowExceptionForHR(status);
                    }

                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    pending.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Thread.Sleep(s_poll);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(pending);
        }
    }

    /// <summary>VSS calls block (seconds while the volume's writes are held): on a thread of their own, not the pool's.</summary>
    private static Task<T> Run<T>(Func<T> work) => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [GeneratedRegex(@"^(?<volume>\\\\\?\\Volume\{[0-9A-Fa-f-]{36}\}\\)(?<relative>.*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex VolumePath();

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot of {Directory} taken: {Device}")]
    private partial void LogTaken(string directory, string device);

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot {Device} deleted")]
    private static partial void LogDeleted(ILogger logger, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot {Device} could not be deleted now (Windows deletes it with the agent's requester): {Reason}")]
    private static partial void LogNotDeleted(ILogger logger, string device, string reason);

    private sealed record Taken(IVssBackupComponents Backup, Guid SnapshotId, string Device);

    /// <summary>
    /// A VSS step that failed, with its HRESULT: COM interop throws a failed HRESULT as whatever exception .NET maps it to
    /// (E_ACCESSDENIED: UnauthorizedAccessException), so the code is taken from it and the step named.
    /// </summary>
    private sealed class VssFailure : Exception
    {
        public VssFailure(string step, Exception inner)
            : base($"{step} failed (0x{inner.HResult:X8}): {inner.Message}", inner) => HResult = inner.HResult;
    }

    private sealed class Snapshot(Taken taken, string directory, ILogger logger) : IVolumeSnapshot
    {
        private int _disposed;

        public string Directory { get; } = directory;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await Run(() =>
            {
                try
                {
                    var deleted = 0;
                    var kept = Guid.Empty;
                    taken.Backup.DeleteSnapshots(taken.SnapshotId, VSS_OBJECT_TYPE.VSS_OBJECT_SNAPSHOT, true, in deleted, in kept);
                    LogDeleted(logger, taken.Device);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Best effort, never a failed backup: interop throws mapped types (UnauthorizedAccessException…), and
                    // an auto-release snapshot goes with its requester anyway.
                    LogNotDeleted(logger, taken.Device, ex.Message);
                }
                finally
                {
                    // An auto-release snapshot goes with its requester.
                    Marshal.FinalReleaseComObject(taken.Backup);
                }

                return true;
            });
        }
    }
}
