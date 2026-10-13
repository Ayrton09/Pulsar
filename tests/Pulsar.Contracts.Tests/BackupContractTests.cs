using System.Text;

namespace Pulsar.Contracts.Tests;

/// <summary>What backups carry between the agent and the panel, old and new agents alike (Phase 15).</summary>
public sealed class BackupContractTests
{
    [Fact]
    public void HowABackupReadTheFilesTravelsWithItsResult()
    {
        CommandResult made = new BackupResult(10, 2, 0, "ab", Capture: new BackupCapture(BackupCaptureMode.Live, BackupSnapshotErrors.NoSpace,
            GameSaveOutcome.Unconfirmed));

        var json = ProtocolJson.Serialize(made);
        var back = ProtocolJson.Deserialize<CommandResult>(json);

        Assert.Equal(made, back);
        Assert.Contains("\"mode\":\"live\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.Contains("\"save\":\"unconfirmed\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderAgentsResultHasNoCapture()
    {
        var json = """{"$type":"backups.created","size":10,"fileCount":2,"skippedFiles":1,"sha256":"ab"}"""u8;

        var back = Assert.IsType<BackupResult>(ProtocolJson.Deserialize<CommandResult>(json));

        Assert.Null(back.Capture);
    }

    [Fact]
    public void ARestoreCarriesTheBackupsSizeOnlyWhenThePanelSendsIt()
    {
        var older = """{"serverId":"0192f1a0-0000-7000-8000-000000000001","backupId":"0192f1a0-0000-7000-8000-000000000002","sha256":"ab"}"""u8;

        Assert.Null(ProtocolJson.Deserialize<RestoreBackupCommand>(older).SizeBytes);
        Assert.Equal(4096, ProtocolJson.Deserialize<RestoreBackupCommand>(ProtocolJson.Serialize(
            new RestoreBackupCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "ab", SizeBytes: 4096))).SizeBytes);
    }

    [Fact]
    public void ARestoreIsAnOverlayUnlessAskedToBeClean()
    {
        var json = """{"serverId":"0192f1a0-0000-7000-8000-000000000001","backupId":"0192f1a0-0000-7000-8000-000000000002","sha256":"ab"}"""u8;

        Assert.False(ProtocolJson.Deserialize<RestoreBackupCommand>(json).Clean);
        Assert.True(ProtocolJson.Deserialize<RestoreBackupCommand>(ProtocolJson.Serialize(
            new RestoreBackupCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "ab", Clean: true))).Clean);
    }
}
