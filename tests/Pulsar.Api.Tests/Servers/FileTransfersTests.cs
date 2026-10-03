using Microsoft.Extensions.Time.Testing;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Transfer tickets (docs/architecture/03, "Large transfers"): single use, for one node, for a short time.</summary>
public sealed class FileTransfersTests
{
    private static readonly NodeId s_node = new(Guid.NewGuid());
    private static readonly NodeId s_other = new(Guid.NewGuid());
    private readonly FakeTimeProvider _clock = new();
    private readonly FileTransfers _transfers;

    public FileTransfersTests() => _transfers = new FileTransfers(_clock);

    [Fact]
    public async Task ATicketWorksOnceAndOnlyForItsNode()
    {
        using var content = new MemoryStream([1, 2, 3]);
        using var upload = _transfers.BeginUpload(s_node, content, 3);
        using var download = _transfers.BeginDownload(s_node);

        Assert.True(TransferTicket.IsWellFormed(upload.Ticket));
        Assert.NotEqual(upload.Ticket, download.Ticket);
        Assert.Null(_transfers.ClaimDownload(upload.Ticket, s_node)); // not that kind of transfer, and now burnt
        Assert.Null(_transfers.ClaimUpload(upload.Ticket, s_node));
        Assert.Null(_transfers.ClaimDownload(download.Ticket, s_other)); // another node: burnt too
        Assert.Null(_transfers.ClaimDownload(download.Ticket, s_node));

        using var second = _transfers.BeginUpload(s_node, content, 3);
        var claimed = _transfers.ClaimUpload(second.Ticket, s_node);
        Assert.Same(second, claimed);
        await second.WaitForClaimAsync(TestContext.Current.CancellationToken);
        Assert.Null(_transfers.ClaimUpload(second.Ticket, s_node));
    }

    [Fact]
    public async Task AnUnclaimedTicketExpires()
    {
        using var download = _transfers.BeginDownload(s_node);
        var waiting = download.WaitForClaimAsync(TestContext.Current.CancellationToken);

        _clock.Advance(FileTransfers.ClaimTimeout + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
        Assert.Null(_transfers.ClaimDownload(download.Ticket, s_node));
    }

    [Fact]
    public async Task OnlyTheNodeOfAnUploadCanReportOnIt()
    {
        using var content = new MemoryStream();
        using var upload = _transfers.BeginUpload(s_node, content, 0);
        Assert.NotNull(_transfers.ClaimUpload(upload.Ticket, s_node));

        _transfers.Complete(s_other, new TransferResult(upload.Ticket, FileErrors.Failed, "not yours", null));
        Assert.False(upload.Result.IsCompleted);

        var file = new FileEntryResult(new FileEntry("a.cfg", FileEntryKind.File, 0, DateTimeOffset.UnixEpoch), "0-0");
        _transfers.Complete(s_node, new TransferResult(upload.Ticket, null, null, file));
        Assert.Equal(file, (await upload.Result).File);
    }

    [Fact]
    public async Task ADownloadHandsTheAgentsContentToTheBrowserAndReleasesTheAgentAfter()
    {
        using var download = _transfers.BeginDownload(s_node);
        var claimed = _transfers.ClaimDownload(download.Ticket, s_node)!;
        using var body = new MemoryStream([1, 2, 3]);

        var agentDone = claimed.DeliverAsync(body, 3);
        var (content, length) = await download.Content;
        Assert.Same(body, content);
        Assert.Equal(3, length);
        Assert.False(agentDone.IsCompleted);

        download.Finish(complete: true);
        Assert.True(await agentDone);
    }

    [Fact]
    public async Task ABrowserThatLeavesReleasesTheAgent()
    {
        var download = _transfers.BeginDownload(s_node);
        var claimed = _transfers.ClaimDownload(download.Ticket, s_node)!;
        using var body = new MemoryStream();
        var agentDone = claimed.DeliverAsync(body, 0);

        download.Dispose();

        Assert.False(await agentDone);
    }
}
