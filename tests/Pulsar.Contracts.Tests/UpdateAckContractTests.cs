using System.Text;

namespace Pulsar.Contracts.Tests;

public sealed class UpdateAckContractTests
{
    [Fact]
    public void ARefusedRetryCarriesTheCurrentRunsIdentityAndProgress()
    {
        var report = new UpdateReport(UpdateStatuses.Installing, "1.0.28", "1.0.29", null, null, DateTimeOffset.UtcNow, Guid.NewGuid());
        var ack = new CommandAck(Guid.NewGuid(), AckStatus.Rejected, "update.in_progress", "Already running.", Update: report);

        Assert.Equal(ack, ProtocolJson.Deserialize<CommandAck>(ProtocolJson.Serialize(ack)));
    }

    [Fact]
    public void AnOlderAgentsAnswerStillHasNoCurrentRun()
    {
        var old = Encoding.UTF8.GetBytes("""
            {"commandId":"0192f1a0-0000-7000-8000-000000000001","status":"rejected","errorCode":"update.in_progress","message":"Already running."}
            """);

        var ack = ProtocolJson.Deserialize<CommandAck>(old);

        Assert.Equal("update.in_progress", ack.ErrorCode);
        Assert.Null(ack.Update);
    }
}
