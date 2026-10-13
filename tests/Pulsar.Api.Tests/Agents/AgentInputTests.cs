using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// Text and lists an agent chooses are bounded where they enter the panel, before browsers, the audit log, the panel's
/// log and notifications see them (docs/architecture/08, T32).
/// </summary>
public sealed class AgentInputTests
{
    [Theory]
    [InlineData("files.not_found", "files.not_found")]
    [InlineData("steamcmd.exit_0x6", "steamcmd.exit_0x6")]
    [InlineData("Files.NotFound", AgentInput.InvalidErrorCode)]
    [InlineData("files.not_found\nINF forged line", AgentInput.InvalidErrorCode)]
    [InlineData("files not found", AgentInput.InvalidErrorCode)]
    [InlineData("", AgentInput.InvalidErrorCode)]
    [InlineData(null, null)]
    public void ErrorCodesKeepTheProtocolsShapeOrBecomeAGenericOne(string? code, string? expected)
    {
        Assert.Equal(expected, AgentInput.ErrorCode(code));
    }

    [Fact]
    public void ALongErrorCodeBecomesTheGenericOne()
    {
        Assert.Equal(AgentInput.InvalidErrorCode, AgentInput.ErrorCode(new string('a', AgentInput.MaxErrorCodeLength + 1)));
        Assert.Equal(new string('a', AgentInput.MaxErrorCodeLength), AgentInput.ErrorCode(new string('a', AgentInput.MaxErrorCodeLength)));
    }

    [Fact]
    public void AnAcknowledgementLosesWhatWouldForgeALogLine()
    {
        var ack = AgentInput.Ack(new CommandAck(Guid.NewGuid(), AckStatus.Rejected, "x\r\n[12:00:00 WRN] forged", "line one\r\nline two\u001b[31m" + new string('m', 5000)));

        Assert.Equal(AgentInput.InvalidErrorCode, ack.ErrorCode);
        Assert.DoesNotContain(ack.Message!, char.IsControl);
        Assert.StartsWith("line one", ack.Message, StringComparison.Ordinal);
        Assert.True(ack.Message!.Length <= AgentInput.MaxMessageLength);
    }

    [Fact]
    public void ProgressHasAShortStageWithoutControlOrDirectionCharacters()
    {
        var progress = AgentInput.Progress(new OperationProgress(Guid.NewGuid(), "steam\u202Ecmd\n" + new string('s', 500), 50, null));

        Assert.True(progress.Stage.Length <= AgentInput.MaxStageLength);
        Assert.StartsWith("steamcmd", progress.Stage, StringComparison.Ordinal);
        Assert.Equal(50, progress.Percent);
    }

    [Fact]
    public void ACompletedOperationOrTransferKeepsOnlyACodeOfTheProtocolsShape()
    {
        Assert.Equal(AgentInput.InvalidErrorCode,
            AgentInput.Completed(new OperationCompleted(Guid.NewGuid(), null, OperationOutcome.Failed, "BAD CODE", "message")).ErrorCode);
        Assert.Equal("files.too_large", AgentInput.Transfer(new TransferResult("ticket", "files.too_large", null, null)).ErrorCode);
        Assert.Equal(AgentInput.InvalidErrorCode, AgentInput.Transfer(new TransferResult("ticket", "files.too_large\n", null, null)).ErrorCode);
    }

    [Fact]
    public void NodeMetricsListAFewDrivesWithShortNames()
    {
        var disks = Enumerable.Range(0, 100)
            .Select(i => new DiskUsage($"D:\\{i}\u0007\n" + new string('x', 300), 100, 50, ["servers", "backups", "x\nforged", .. Enumerable.Repeat("more", 50)]))
            .ToList();

        var metrics = AgentInput.Metrics(new NodeMetrics(DateTimeOffset.UtcNow, 5, 1, 2, TimeSpan.FromHours(1), disks));

        Assert.Equal(AgentInput.MaxDisks, metrics.Disks!.Count);
        Assert.All(metrics.Disks, d =>
        {
            Assert.True(d.Root.Length <= AgentInput.MaxDiskRootLength);
            Assert.DoesNotContain(d.Root, char.IsControl);
            Assert.True(d.Uses.Count <= AgentInput.MaxDiskUses);
            Assert.All(d.Uses, u => Assert.DoesNotContain(u, char.IsControl));
        });
        Assert.Equal("D:\\0", metrics.Disks[0].Root[..4]);
        Assert.Null(AgentInput.Metrics(new NodeMetrics(DateTimeOffset.UtcNow, 5, 1, 2, TimeSpan.FromHours(1))).Disks);
    }
}
