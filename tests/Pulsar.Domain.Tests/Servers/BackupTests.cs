using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Servers;

public sealed class BackupTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ABackupRunsWhileTheServerDoesAndOnlyBlocksOtherOperations()
    {
        var server = InstalledServer();
        server.Observe(ServerRuntimeState.Running, ServerStateReason.Ready, null, 1, s_now, snapshot: false);
        var backup = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);

        Assert.True(server.BeginOperation(backup, s_now).IsSuccess);
        Assert.Equal(OperationType.BackupCreate, server.ActiveOperationType);
        Assert.True(server.RequestStart(s_now).IsSuccess);
        Assert.True(server.RequestRestart(s_now).IsSuccess);
        var update = Operation.Create(OperationType.ServerUpdate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        Assert.Equal(ServerErrors.Busy, server.BeginOperation(update, s_now).Error);
        Assert.True(backup.IsCancellable);

        backup.Complete(OperationStatus.Succeeded, null, null, s_now);
        server.OperationCompleted(backup, s_now);
        Assert.Null(server.ActiveOperationId);
        Assert.Null(server.ActiveOperationType);
        Assert.Equal(InstallState.Installed, server.InstallState);
    }

    [Fact]
    public void ARestoreNeedsAStoppedServerThatCannotStartUntilItEnds()
    {
        var server = InstalledServer();
        server.Observe(ServerRuntimeState.Running, ServerStateReason.Ready, null, 1, s_now, snapshot: false);
        var restore = Operation.Create(OperationType.BackupRestore, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        Assert.Equal(ServerErrors.MustBeStopped, server.BeginOperation(restore, s_now).Error);

        server.Observe(ServerRuntimeState.Crashed, ServerStateReason.Crashed, 1, 2, s_now, snapshot: false);
        Assert.True(server.BeginOperation(restore, s_now).IsSuccess);
        Assert.Equal(ServerErrors.Busy, server.RequestStart(s_now).Error);
        Assert.False(restore.IsCancellable); // half a restore is worse than either side of it

        restore.Complete(OperationStatus.Failed, "backups.corrupt", null, s_now);
        server.OperationCompleted(restore, s_now);
        Assert.Equal(InstallState.Installed, server.InstallState);
        Assert.True(server.RequestStart(s_now).IsSuccess);
    }

    [Fact]
    public void BackupsAreForInstalledServers()
    {
        var server = Server.Create(NodeId.New(), TemplateId.New(), "Test", null, Server.NewShortId(), s_now).Value;

        foreach (var type in new[] { OperationType.BackupCreate, OperationType.BackupRestore })
        {
            var operation = Operation.Create(type, server.NodeId, server.Id, null, OperationRequester.User, s_now);
            Assert.Equal(ServerErrors.NotInstalled, server.BeginOperation(operation, s_now).Error);
        }
    }

    [Fact]
    public void TheBackupLimitIsAPositiveNumberOrNone()
    {
        var server = InstalledServer();

        Assert.True(server.SetBackupLimit(5, s_now).IsSuccess);
        Assert.Equal(5, server.BackupLimit);
        Assert.True(server.SetBackupLimit(null, s_now).IsSuccess);
        Assert.Null(server.BackupLimit);
        Assert.Equal(ServerErrors.BackupLimitInvalid, server.SetBackupLimit(0, s_now).Error);
        Assert.Equal(ServerErrors.BackupLimitInvalid, server.SetBackupLimit(Server.MaxBackupLimit + 1, s_now).Error);
    }

    [Fact]
    public void ABackupRecordsWhatTheNodeMadeAndHowLongItTook()
    {
        var server = InstalledServer();
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        var backup = Backup.Create(server, "  Before the update  ", operation, null, OperationRequester.User, s_now).Value;
        Assert.Equal("Before the update", backup.Name);
        Assert.Equal(BackupStatus.Creating, backup.Status);
        Assert.Null(backup.Duration);

        backup.Completed(1234, 7, 1, [1, 2, 3], s_now.AddSeconds(30));

        Assert.Equal(BackupStatus.Completed, backup.Status);
        Assert.Equal(1234, backup.SizeBytes);
        Assert.Equal(7, backup.FileCount);
        Assert.Equal(1, backup.SkippedFiles);
        Assert.Equal([1, 2, 3], backup.Sha256);
        Assert.Equal(TimeSpan.FromSeconds(30), backup.Duration);

        // A late report of the same operation changes nothing.
        backup.Ended(BackupStatus.Failed, "late", s_now.AddMinutes(5));
        Assert.Equal(BackupStatus.Completed, backup.Status);
    }

    [Fact]
    public void ABackupRecordsHowItsFilesWereRead()
    {
        var snapshot = NewBackup();
        var live = NewBackup();
        var forged = NewBackup();
        var older = NewBackup();

        snapshot.Completed(1, 1, 0, [1], s_now, BackupCopyMode.Snapshot, "snapshot.no_space", GameSaveResult.Confirmed);
        live.Completed(1, 1, 2, [1], s_now, BackupCopyMode.Live, "snapshot.no_space", GameSaveResult.Unconfirmed);
        forged.Completed(1, 1, 0, [1], s_now, BackupCopyMode.Live, "<script>" + new string('x', 100));
        older.Completed(1, 1, 0, [1], s_now);

        Assert.Equal((BackupCopyMode.Snapshot, null, GameSaveResult.Confirmed), (snapshot.CopyMode, snapshot.SnapshotError, snapshot.GameSave));
        Assert.Equal((BackupCopyMode.Live, "snapshot.no_space", GameSaveResult.Unconfirmed), (live.CopyMode, live.SnapshotError, live.GameSave));
        Assert.Null(forged.SnapshotError); // the agent's word is shown as a reason: only a plain code
        Assert.Equal((null, null, null), (older.CopyMode, older.SnapshotError, older.GameSave));
    }

    [Fact]
    public void ABackupThatLeftFilesOutCannotBeRestoredClean()
    {
        var complete = NewBackup();
        var partial = NewBackup();
        complete.Completed(1, 3, 0, [1], s_now, BackupCopyMode.Snapshot);
        partial.Completed(1, 3, 2, [1], s_now, BackupCopyMode.Live, "snapshot.not_permitted");

        Assert.True(complete.CheckCleanRestore().IsSuccess);
        Assert.Equal(ServerErrors.CleanRestoreIncomplete, partial.CheckCleanRestore().Error);
    }

    private static Backup NewBackup()
    {
        var server = InstalledServer();
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        return Backup.Create(server, "a", operation, null, OperationRequester.User, s_now).Value;
    }

    [Fact]
    public void AFailedOrCancelledBackupKeepsWhy()
    {
        var server = InstalledServer();
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        var failed = Backup.Create(server, "a", operation, null, OperationRequester.User, s_now).Value;
        var cancelled = Backup.Create(server, "b", operation, null, OperationRequester.User, s_now).Value;

        failed.Ended(BackupStatus.Failed, new string('x', 2000), s_now);
        cancelled.Ended(BackupStatus.Cancelled, null, s_now);

        Assert.Equal(BackupStatus.Failed, failed.Status);
        Assert.Equal(Backup.MaxErrorMessageLength, failed.ErrorMessage!.Length);
        Assert.Equal(BackupStatus.Cancelled, cancelled.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => failed.Ended(BackupStatus.Completed, null, s_now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void ABackupNameIsOneShortLine(string name)
    {
        var server = InstalledServer();
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);

        Assert.Equal(ServerErrors.BackupNameInvalid, Backup.Create(server, name, operation, null, OperationRequester.User, s_now).Error);
        Assert.Equal(ServerErrors.BackupNameInvalid,
            Backup.Create(server, new string('a', Backup.MaxNameLength + 1), operation, null, OperationRequester.User, s_now).Error);
    }

    [Fact]
    public void OnlyFinishedBackupsAreLockedOrRestored()
    {
        var server = InstalledServer();
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        var backup = Backup.Create(server, "a", operation, null, OperationRequester.User, s_now).Value;

        Assert.Equal(ServerErrors.BackupNotCompleted, backup.SetLocked(true).Error);
        backup.Completed(1, 1, 0, [1], s_now);
        Assert.True(backup.SetLocked(true).IsSuccess);
        Assert.True(backup.IsLocked);
    }

    private static Server InstalledServer()
    {
        var server = Server.Create(NodeId.New(), TemplateId.New(), "Test", null, Server.NewShortId(), s_now).Value;
        var install = Operation.Create(OperationType.ServerInstall, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        server.BeginOperation(install, s_now);
        install.Complete(OperationStatus.Succeeded, null, null, s_now);
        server.OperationCompleted(install, s_now);
        return server;
    }
}
