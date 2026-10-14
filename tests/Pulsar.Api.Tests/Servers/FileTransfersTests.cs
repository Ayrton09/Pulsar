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
        using var upload = _transfers.BeginUpload(s_node, null, content, 3)!;
        using var download = _transfers.BeginDownload(s_node, null)!;

        Assert.True(TransferTicket.IsWellFormed(upload.Ticket));
        Assert.NotEqual(upload.Ticket, download.Ticket);
        Assert.Null(_transfers.ClaimDownload(upload.Ticket, s_node)); // not that kind of transfer, and now burnt
        Assert.Null(_transfers.ClaimUpload(upload.Ticket, s_node));
        Assert.Null(_transfers.ClaimDownload(download.Ticket, s_other)); // another node: burnt too
        Assert.Null(_transfers.ClaimDownload(download.Ticket, s_node));

        using var second = _transfers.BeginUpload(s_node, null, content, 3)!;
        var claimed = _transfers.ClaimUpload(second.Ticket, s_node);
        Assert.Same(second, claimed);
        await second.WaitForClaimAsync(TestContext.Current.CancellationToken);
        Assert.Null(_transfers.ClaimUpload(second.Ticket, s_node));
    }

    [Fact]
    public void TransfersAreLimitedPerNodeAndPerUserOfTheNodeUntilTheyEnd()
    {
        var transfers = new FileTransfers(_clock, new FileTransferLimits(PerNode: 4, PerUser: 2));
        var alice = new UserId(Guid.NewGuid());
        var bob = new UserId(Guid.NewGuid());
        using var content = new MemoryStream();

        var first = transfers.BeginUpload(s_node, alice, content, 0);
        using var second = transfers.BeginDownload(s_node, alice);
        Assert.NotNull(first);
        Assert.NotNull(second);
        // Her share of the node is taken; another node has its own.
        Assert.Null(transfers.BeginUpload(s_node, alice, content, 0));
        Assert.Null(transfers.BeginDownload(s_node, alice));
        using var elsewhere = transfers.BeginDownload(s_other, alice);
        Assert.NotNull(elsewhere);

        // The node's share is taken by everyone together, the panel's own transfers too.
        using var bobs = transfers.BeginDownload(s_node, bob);
        using var system = transfers.BeginDownload(s_node, null);
        Assert.NotNull(bobs);
        Assert.NotNull(system);
        Assert.Null(transfers.BeginDownload(s_node, bob));
        Assert.Null(transfers.BeginDownload(s_node, null));

        // A transfer gives its place back once, when it ends: to a user with none running, not to Bob's second (it
        // would leave none for the others).
        first!.Dispose();
        first.Dispose();
        Assert.Null(transfers.BeginDownload(s_node, bob));
        using var carols = transfers.BeginDownload(s_node, new UserId(Guid.NewGuid()));
        Assert.NotNull(carols);
        Assert.Null(transfers.BeginDownload(s_node, null));
    }

    [Fact]
    public async Task AnUnclaimedTransferGivesItsPlaceBack()
    {
        var transfers = new FileTransfers(_clock, new FileTransferLimits(PerNode: 1, PerUser: 1));
        var download = transfers.BeginDownload(s_node, null)!;
        var waiting = download.WaitForClaimAsync(TestContext.Current.CancellationToken);
        Assert.Null(transfers.BeginDownload(s_node, null));

        _clock.Advance(FileTransfers.ClaimTimeout + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
        download.Dispose();

        using var next = transfers.BeginDownload(s_node, null);
        Assert.NotNull(next);
    }

    [Fact]
    public void TheDefaultsLeaveANodesConnectionsForItsHubAndRotation()
    {
        Assert.Equal(new FileTransferLimits(8, 3), FileTransferLimits.Default);
        Assert.Equal(new FileTransferLimits(8, 3), FileTransferLimits.ForNodeConnections(16));
        Assert.Equal(new FileTransferLimits(2, 2), FileTransferLimits.ForNodeConnections(4));
        Assert.Equal(new FileTransferLimits(1, 1), FileTransferLimits.ForNodeConnections(1));
    }

    [Fact]
    public void AUsersFurtherTransfersLeaveTwoPlacesForUsersWithNone()
    {
        var transfers = new FileTransfers(_clock, FileTransferLimits.Default);
        var users = Enumerable.Range(0, 4).Select(_ => new UserId(Guid.NewGuid())).ToArray();
        int Start(UserId user, int wanted) => Enumerable.Range(0, wanted).Count(_ => transfers.BeginDownload(s_node, user) is not null);

        // Three each for the first two users: two places stay free.
        Assert.Equal(3, Start(users[0], 4));
        Assert.Equal(3, Start(users[1], 4));
        // A third user gets one: a second would leave fewer than two for users with none running.
        Assert.Equal(1, Start(users[2], 3));
        // So a fourth user still gets in.
        Assert.Equal(1, Start(users[3], 3));
        Assert.Equal(8, transfers.Running(s_node));
    }

    [Fact]
    public void EndingAUserAbortsTheirTransfersOnly()
    {
        var alice = new UserId(Guid.NewGuid());
        var bob = new UserId(Guid.NewGuid());
        using var content = new MemoryStream();
        using var upload = _transfers.BeginUpload(s_node, alice, content, 3)!;
        using var download = _transfers.BeginDownload(s_other, alice)!;
        using var bobs = _transfers.BeginDownload(s_node, bob)!;
        using var panels = _transfers.BeginDownload(s_node, null)!;

        _transfers.EndUser(alice);

        Assert.True(upload.Aborted.IsCancellationRequested);
        Assert.True(download.Aborted.IsCancellationRequested);
        Assert.False(bobs.Aborted.IsCancellationRequested);
        Assert.False(panels.Aborted.IsCancellationRequested);

        // A role changed: every user's transfers, not the panel's own.
        _transfers.EndAll();
        Assert.True(bobs.Aborted.IsCancellationRequested);
        Assert.False(panels.Aborted.IsCancellationRequested);
    }

    [Fact]
    public void RevokingAnApiKeyAbortsTheTransfersMadeWithIt()
    {
        var alice = new UserId(Guid.NewGuid());
        var key = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var withKey = _transfers.BeginDownload(s_node, alice, key)!;
        using var withOther = _transfers.BeginDownload(s_node, alice, other)!;
        using var inBrowser = _transfers.BeginDownload(s_other, alice)!;

        _transfers.EndApiKey(key);
        Assert.True(withKey.Aborted.IsCancellationRequested);
        Assert.False(withOther.Aborted.IsCancellationRequested);

        _transfers.EndApiKeys(alice);
        Assert.True(withOther.Aborted.IsCancellationRequested);
        Assert.False(inBrowser.Aborted.IsCancellationRequested);
    }

    [Fact]
    public async Task ATransferLastsNoLongerThanItsSizeAtTheLeastRate()
    {
        Assert.Equal(FileTransfers.MinimumDuration, FileTransfers.MaxDuration(1024));
        Assert.Equal(TimeSpan.FromSeconds(1600), FileTransfers.MaxDuration(100L * 1024 * 1024));

        using var content = new MemoryStream();
        using var upload = _transfers.BeginUpload(s_node, null, content, 100L * 1024 * 1024)!;
        _clock.Advance(TimeSpan.FromSeconds(1599));
        Assert.False(upload.Aborted.IsCancellationRequested);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(upload.Aborted.IsCancellationRequested);

        // A download's size is known once the agent sends it: counted from then.
        using var download = _transfers.BeginDownload(s_node, null)!;
        Assert.NotNull(_transfers.ClaimDownload(download.Ticket, s_node));
        _ = download.DeliverAsync(content, 1024);
        _clock.Advance(FileTransfers.MinimumDuration - TimeSpan.FromSeconds(1));
        Assert.False(download.Aborted.IsCancellationRequested);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(download.Aborted.IsCancellationRequested);
        Assert.False(await download.DeliverAsync(content, 1024));
    }

    [Fact]
    public async Task AnUnclaimedTicketExpires()
    {
        using var download = _transfers.BeginDownload(s_node, null)!;
        var waiting = download.WaitForClaimAsync(TestContext.Current.CancellationToken);

        _clock.Advance(FileTransfers.ClaimTimeout + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
        Assert.Null(_transfers.ClaimDownload(download.Ticket, s_node));
    }

    [Fact]
    public async Task OnlyTheNodeOfAnUploadCanReportOnIt()
    {
        using var content = new MemoryStream();
        using var upload = _transfers.BeginUpload(s_node, null, content, 0)!;
        Assert.NotNull(_transfers.ClaimUpload(upload.Ticket, s_node));

        _transfers.Complete(s_other, new TransferResult(upload.Ticket, FileErrors.Failed, "not yours", null));
        Assert.False(upload.Result.IsCompleted);

        var file = new FileEntryResult(new FileEntry("a.cfg", FileEntryKind.File, 0, DateTimeOffset.UnixEpoch), "0-0");
        _transfers.Complete(s_node, new TransferResult(upload.Ticket, null, null, file));
        Assert.Equal(file, (await upload.Result).File);
    }

    [Fact]
    public void AReportWithoutATicketIsIgnored()
    {
        using var content = new MemoryStream();
        using var upload = _transfers.BeginUpload(s_node, null, content, 0)!;

        _transfers.Complete(s_node, new TransferResult(null!, FileErrors.Failed, "no ticket", null));

        Assert.False(upload.Result.IsCompleted);
    }

    [Fact]
    public async Task ADownloadHandsTheAgentsContentToTheBrowserAndReleasesTheAgentAfter()
    {
        using var download = _transfers.BeginDownload(s_node, null)!;
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
    public async Task AnAgentThatClaimsADownloadButSendsNothingIsNotWaitedForForever()
    {
        using var download = _transfers.BeginDownload(s_node, null)!;
        Assert.NotNull(_transfers.ClaimDownload(download.Ticket, s_node));
        var waiting = download.WaitForContentAsync(TestContext.Current.CancellationToken);

        _clock.Advance(FileTransfers.ClaimTimeout + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
    }

    [Fact]
    public async Task AnUploadWaitsWhileItMovesAndGivesUpWhenItStalls()
    {
        using var content = new MemoryStream([1, 2, 3]);
        using var upload = _transfers.BeginUpload(s_node, null, content, 1L << 30)!;
        Assert.NotNull(_transfers.ClaimUpload(upload.Ticket, s_node));
        var waiting = upload.WaitUntilSentAsync(TestContext.Current.CancellationToken);

        // A slow upload that keeps moving is waited for, within the time its size allows.
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(FileTransfers.IdleTimeout - TimeSpan.FromSeconds(30));
            upload.Progressed();
            _clock.Advance(TimeSpan.FromSeconds(20));
            await Task.Delay(100, TestContext.Current.CancellationToken); // a deadline that passed is checked against the progress
            Assert.False(waiting.IsCompleted, waiting.Exception?.ToString() ?? $"iteration {i}");
        }

        // An agent that stops reading is not.
        _clock.Advance(FileTransfers.IdleTimeout);
        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
    }

    [Fact]
    public async Task ABrowserThatLeavesReleasesTheAgent()
    {
        var download = _transfers.BeginDownload(s_node, null)!;
        var claimed = _transfers.ClaimDownload(download.Ticket, s_node)!;
        using var body = new MemoryStream();
        var agentDone = claimed.DeliverAsync(body, 0);

        download.Dispose();

        Assert.False(await agentDone);
    }
}
