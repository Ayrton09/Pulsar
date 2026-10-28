using Pulsar.Domain.Common;
using Pulsar.Domain.Updates;

namespace Pulsar.Domain.Tests.Updates;

/// <summary>
/// What a node's update takes from its agent (Phase 19): each request's own reports, in order. A result the agent sends
/// again after reconnecting, a step that arrives late or twice, never ends or moves back a later request, of the same
/// version too; no clock of the node's is compared with the panel's.
/// </summary>
public sealed class NodeUpdateTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARetryOfTheSameVersionTakesOnlyItsOwnReportsInOrder()
    {
        var update = NodeUpdate.Request(NodeId.New(), "1.0.2", "1.0.3", null, s_now);
        var first = update.AttemptId!.Value;
        Report(update, NodeUpdate.Downloading, first);
        Report(update, NodeUpdate.Installing, first);
        Assert.True(Report(update, NodeUpdate.RolledBack, first, "The panel did not start."));

        update.RequestAgain("1.0.2", "1.0.3", null, s_now.AddMinutes(10));
        var second = update.AttemptId!.Value;
        Assert.NotEqual(first, second);

        // The first one's result, sent again after a reconnection, at any step of the second: not the second's.
        Assert.False(Report(update, NodeUpdate.RolledBack, first, "The panel did not start."));
        Assert.Equal((NodeUpdate.Requested, null), (update.Status, update.Error));
        Assert.True(Report(update, NodeUpdate.Downloading, second));
        Assert.False(Report(update, NodeUpdate.RolledBack, first));
        Assert.True(Report(update, NodeUpdate.Installing, second));
        Assert.False(Report(update, NodeUpdate.RolledBack, first));
        Assert.Equal(NodeUpdate.Installing, update.Status);

        // A step that arrives late, or twice, moves nothing back.
        Assert.False(Report(update, NodeUpdate.Downloading, second));
        Assert.False(Report(update, NodeUpdate.Installing, second));
        Assert.Equal(NodeUpdate.Installing, update.Status);

        Assert.True(Report(update, NodeUpdate.Succeeded, second));
        Assert.Equal((NodeUpdate.Succeeded, null), (update.Status, update.Error));

        // Its end sent again, an end of the same request after it (an installer and the agent both reporting), the first
        // one's sent again: the end stays.
        Assert.False(Report(update, NodeUpdate.Succeeded, second));
        Assert.False(Report(update, NodeUpdate.Failed, second, "The installer ended with exit code 1."));
        Assert.False(Report(update, NodeUpdate.RolledBack, first));
        Assert.Equal((NodeUpdate.Succeeded, null), (update.Status, update.Error));
    }

    [Fact]
    public void ALateEndOfThisRequestIsTakenWithoutComparingClocks()
    {
        var update = NodeUpdate.Request(NodeId.New(), "1.0.2", "1.0.3", null, s_now);
        var attempt = update.AttemptId!.Value;
        Report(update, NodeUpdate.Downloading, attempt);
        Report(update, NodeUpdate.Installing, attempt);

        // Past the deadline (the node was off for hours), its end still names it: taken.
        Assert.False(update.IsUnderWay(s_now + NodeUpdate.Deadline + TimeSpan.FromHours(3)));
        Assert.True(update.Reported(NodeUpdate.Succeeded, "1.0.2", "1.0.3", null, attempt, s_now + NodeUpdate.Deadline + TimeSpan.FromHours(3)));
        Assert.Equal(NodeUpdate.Succeeded, update.Status);
    }

    [Fact]
    public void ReportsWithoutARequestIdAreTakenInOrderOnly()
    {
        // An agent, or a release's installer, older than request ids.
        var update = NodeUpdate.Request(NodeId.New(), "1.0.2", "1.0.3", null, s_now);
        Report(update, NodeUpdate.RolledBack, null);
        Assert.Equal(NodeUpdate.Requested, update.Status);

        update = NodeUpdate.Request(NodeId.New(), "1.0.2", "1.0.3", null, s_now);
        // An earlier result sent again before the agent said it took this request up: not this one's end.
        Assert.False(Report(update, NodeUpdate.RolledBack, null));
        Assert.False(Report(update, NodeUpdate.Failed, null));
        Assert.True(Report(update, NodeUpdate.Downloading, null));
        // The installer's result before the installer was started: an earlier one.
        Assert.False(Report(update, NodeUpdate.RolledBack, null));
        Assert.False(Report(update, NodeUpdate.Succeeded, null));
        // Another version's steps are another update's.
        Assert.False(update.Reported(NodeUpdate.Installing, "1.0.2", "1.0.4", null, null, s_now));
        Assert.True(Report(update, NodeUpdate.Installing, null));
        Assert.False(Report(update, NodeUpdate.Downloading, null));
        Assert.True(Report(update, NodeUpdate.RolledBack, null, "The agent did not sign in."));
        Assert.Equal(NodeUpdate.RolledBack, update.Status);

        // Ended: this version's result sent again is the one kept (or an earlier one); the setups' newer one is news.
        Assert.False(Report(update, NodeUpdate.Succeeded, null));
        Assert.True(update.Reported(NodeUpdate.Succeeded, "1.0.2", "1.0.4", null, null, s_now));
        Assert.Equal((NodeUpdate.Succeeded, "1.0.4"), (update.Status, update.ToVersion));
    }

    [Fact]
    public void OnceTheRequestReportedWithItsIdAResultWithoutOneIsAnEarlierOne()
    {
        // An installer's result from before request ids, of the same version, sent again after a reconnection.
        var update = NodeUpdate.Request(NodeId.New(), "1.0.2", "1.0.3", null, s_now);
        var attempt = update.AttemptId!.Value;
        Assert.True(Report(update, NodeUpdate.Downloading, attempt));
        Assert.True(update.AttemptReported);
        Assert.False(Report(update, NodeUpdate.Failed, null, "x"));
        Assert.True(Report(update, NodeUpdate.Installing, attempt));
        Assert.False(Report(update, NodeUpdate.RolledBack, null, "The panel did not start."));
        Assert.False(Report(update, NodeUpdate.Succeeded, null));
        Assert.Equal((NodeUpdate.Installing, null), (update.Status, update.Error));

        Assert.True(Report(update, NodeUpdate.Succeeded, attempt));
        // Ended: the setups' result of another version is still news.
        Assert.True(update.Reported(NodeUpdate.Succeeded, "1.0.3", "1.0.4", null, null, s_now));

        // A new request starts without knowing: an agent older than ids answers it without one.
        update.RequestAgain("1.0.4", "1.0.5", null, s_now);
        Assert.False(update.AttemptReported);
        Assert.True(update.Reported(NodeUpdate.Downloading, "1.0.4", "1.0.5", null, null, s_now));
    }

    [Fact]
    public void AFailureWhileDownloadingIsTakenWithoutARequestId()
    {
        var update = NodeUpdate.Request(NodeId.New(), "1.0.2", "1.0.3", null, s_now);
        Report(update, NodeUpdate.Downloading, null);

        Assert.True(Report(update, NodeUpdate.Failed, null, "The release 1.0.3 was refused."));
        Assert.Equal((NodeUpdate.Failed, "The release 1.0.3 was refused."), (update.Status, update.Error));
    }

    [Fact]
    public void ARefusedRequestEndsThereAndItsIdIsNoLaterOnes()
    {
        var update = NodeUpdate.Request(NodeId.New(), "1.0.2", "1.0.3", null, s_now);
        var refused = update.AttemptId!.Value;
        update.Refused("update.in_progress", s_now);
        Assert.Equal((NodeUpdate.Failed, "update.in_progress"), (update.Status, update.Error));
        Assert.False(update.IsUnderWay(s_now));

        update.RequestAgain("1.0.2", "1.0.3", null, s_now);
        Assert.False(Report(update, NodeUpdate.Downloading, refused));
        Assert.True(Report(update, NodeUpdate.Downloading, update.AttemptId));
    }

    [Fact]
    public void ARowThisPanelDidNotAskForTakesWhatComes()
    {
        // The setups' update, or one an earlier panel asked for.
        var update = NodeUpdate.FromReport(NodeId.New(), NodeUpdate.Succeeded, "1.0.2", "1.0.3", null, s_now);
        Assert.Null(update.AttemptId);

        Assert.True(update.Reported(NodeUpdate.RolledBack, "1.0.3", "1.0.4", "x", null, s_now));
        Assert.True(update.Reported(NodeUpdate.Downloading, "1.0.3", "1.0.4", null, Guid.NewGuid(), s_now));
        Assert.False(update.Reported(NodeUpdate.Installing, "1.0.3", "1.0.5", null, null, s_now));
    }

    private static bool Report(NodeUpdate update, string status, Guid? attempt, string? error = null) =>
        update.Reported(status, "1.0.2", "1.0.3", error, attempt, s_now);
}
