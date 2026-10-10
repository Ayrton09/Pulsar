using Pulsar.Domain.Nodes;

namespace Pulsar.Domain.Tests.Nodes;

/// <summary>Reusable enrollment tokens and the nodes they create (Phase 14).</summary>
public sealed class ReusableEnrollmentTokenTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly NodeInventorySnapshot s_inventory =
        new("GAME-01", "Windows Server 2025", "26100", "Test CPU", 8, 16L << 30, null, "1.0.0", 1);

    [Fact]
    public void ATokenIsShownOnceAndOnlyItsHashIsKept()
    {
        var (token, value) = ReusableEnrollmentToken.Issue(" Fleet ", 10, TimeSpan.FromDays(7), requireApproval: true, null, s_now).Value;

        Assert.StartsWith(ReusableEnrollmentToken.Prefix, value, StringComparison.Ordinal);
        Assert.True(value.Length > 50);
        Assert.Equal("Fleet", token.Name);
        Assert.Equal(ReusableEnrollmentToken.Hash(value), token.TokenHash);
        Assert.Equal(value[..(ReusableEnrollmentToken.Prefix.Length + 6)], token.TokenPrefix);
        Assert.Equal(s_now.AddDays(7), token.ExpiresAt);
        Assert.True(token.RequireApproval);
    }

    [Theory]
    [InlineData("", 10, 24, "enrollment_token.name_invalid")]
    [InlineData("Fleet", 0, 24, "enrollment_token.uses_invalid")]
    [InlineData("Fleet", 1001, 24, "enrollment_token.uses_invalid")]
    [InlineData("Fleet", 10, 0, "enrollment_token.lifetime_invalid")]
    [InlineData("Fleet", 10, 91 * 24, "enrollment_token.lifetime_invalid")]
    public void ATokenIsLimited(string name, int uses, int hours, string error)
    {
        var issued = ReusableEnrollmentToken.Issue(name, uses, TimeSpan.FromHours(hours), false, null, s_now);

        Assert.Equal(error, issued.Error?.Code);
    }

    [Fact]
    public void ATokenStopsWorkingWhenUsedUpExpiredOrRevoked()
    {
        var (token, _) = ReusableEnrollmentToken.Issue("Fleet", 2, TimeSpan.FromHours(1), false, null, s_now).Value;
        Assert.True(token.IsUsable(s_now));

        token.Used(s_now);
        token.Used(s_now);
        Assert.False(token.IsUsable(s_now));

        var (expiring, _) = ReusableEnrollmentToken.Issue("Fleet", 2, TimeSpan.FromHours(1), false, null, s_now).Value;
        Assert.False(expiring.IsUsable(s_now.AddHours(1)));

        var (revoked, _) = ReusableEnrollmentToken.Issue("Fleet", 2, TimeSpan.FromHours(1), false, null, s_now).Value;
        revoked.Revoke(s_now);
        revoked.Revoke(s_now.AddMinutes(5));
        Assert.Equal(s_now, revoked.RevokedAt);
        Assert.False(revoked.IsUsable(s_now));
    }

    [Fact]
    public void ANodeThatWaitsForApprovalKeepsWaitingUntilApproved()
    {
        var node = Node.EnrolledWithToken("GAME-01", s_inventory, Guid.NewGuid(), requireApproval: true, s_now).Value;
        Assert.Equal(NodeStatus.PendingApproval, node.Status);
        Assert.False(node.AcceptsConnections);

        node.Disable(s_now);
        node.Enable(s_now);
        Assert.Equal(NodeStatus.PendingApproval, node.Status);

        node.Approve(s_now);
        Assert.Equal(NodeStatus.Active, node.Status);
        Assert.True(node.AcceptsConnections);

        // Approved once: disabling and enabling it brings it back active.
        node.Disable(s_now);
        node.Enable(s_now);
        Assert.Equal(NodeStatus.Active, node.Status);
    }

    [Fact]
    public void ANodeOfATokenWithoutApprovalIsActiveAtOnce()
    {
        var tokenId = Guid.NewGuid();
        var node = Node.EnrolledWithToken("GAME-01", s_inventory, tokenId, requireApproval: false, s_now).Value;

        Assert.Equal((NodeStatus.Active, tokenId), (node.Status, node.EnrollmentTokenId));
        Assert.Equal(s_inventory, node.Inventory);
        Assert.Equal(s_now, node.LastSeenAt);
    }

    [Fact]
    public void ADisabledNodeWaitingForApprovalIsApprovedButStaysDisabled()
    {
        var node = Node.EnrolledWithToken("GAME-01", s_inventory, Guid.NewGuid(), requireApproval: true, s_now).Value;
        node.Disable(s_now);

        node.Approve(s_now);

        Assert.Equal(NodeStatus.Disabled, node.Status);
        node.Enable(s_now);
        Assert.Equal(NodeStatus.Active, node.Status);
    }
}
