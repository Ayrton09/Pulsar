using System.Net;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>How refused agent requests are held to one audit entry per node, reason and address a while (docs/architecture/03).</summary>
public sealed class AgentRefusalBudgetTests
{
    private static readonly IPAddress s_agent = IPAddress.Parse("192.0.2.10");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly NodeId _node = NodeId.New();

    [Fact]
    public void TheFirstRefusalIsRecordedAndTheSameOnesWithinTheWindowAreCounted()
    {
        var budget = new AgentRefusalBudget(_clock);

        Assert.True(budget.TryRecord(_node, "invalid_signature", s_agent, out var first));
        Assert.Equal(0, first);
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False(budget.TryRecord(_node, "invalid_signature", s_agent, out var leftOut));
            Assert.Equal(0, leftOut);
        }

        // The window counts from the recorded entry, not from the last refusal.
        _clock.Advance(AgentRefusalBudget.Window - TimeSpan.FromMinutes(3));
        Assert.True(budget.TryRecord(_node, "invalid_signature", s_agent, out var notRecordedBefore));
        Assert.Equal(3, notRecordedBefore);

        // The count starts again with the new entry.
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(budget.TryRecord(_node, "invalid_signature", s_agent, out _));
        _clock.Advance(AgentRefusalBudget.Window);
        Assert.True(budget.TryRecord(_node, "invalid_signature", s_agent, out var again));
        Assert.Equal(1, again);
    }

    [Fact]
    public void JustBeforeTheWindowEndsTheRefusalIsStillCounted()
    {
        var budget = new AgentRefusalBudget(_clock);
        Assert.True(budget.TryRecord(_node, "invalid_signature", s_agent, out _));

        _clock.Advance(AgentRefusalBudget.Window - TimeSpan.FromTicks(1));

        Assert.False(budget.TryRecord(_node, "invalid_signature", s_agent, out _));
    }

    [Fact]
    public void EachNodeAndReasonHasItsOwnEntry()
    {
        var budget = new AgentRefusalBudget(_clock);
        var other = NodeId.New();

        Assert.True(budget.TryRecord(_node, "invalid_signature", s_agent, out _));
        Assert.True(budget.TryRecord(_node, "credential_or_node_inactive", s_agent, out _));
        Assert.True(budget.TryRecord(other, "invalid_signature", s_agent, out _));
        Assert.False(budget.TryRecord(_node, "invalid_signature", s_agent, out _));
        Assert.False(budget.TryRecord(other, "invalid_signature", s_agent, out _));
    }

    [Fact]
    public void ARefusalFromAnotherAddressIsRecordedWithinTheWindow()
    {
        // The node's own agent keeps being refused; a copied token or key tried from elsewhere is not hidden behind it.
        var budget = new AgentRefusalBudget(_clock);
        var elsewhere = IPAddress.Parse("198.51.100.7");

        Assert.True(budget.TryRecord(_node, "session_certificate_mismatch", s_agent, out _));
        Assert.False(budget.TryRecord(_node, "session_certificate_mismatch", s_agent, out _));
        Assert.True(budget.TryRecord(_node, "session_certificate_mismatch", elsewhere, out _));
        Assert.False(budget.TryRecord(_node, "session_certificate_mismatch", elsewhere, out _));
        Assert.True(budget.TryRecord(_node, "session_certificate_mismatch", null, out _));
    }

    [Fact]
    public void PastTheLimitOfAddressesTheOthersShareOneEntry()
    {
        var budget = new AgentRefusalBudget(_clock);
        var recorded = 0;
        for (var i = 0; i < 1000; i++)
        {
            recorded += budget.TryRecord(_node, "invalid_signature", IPAddress.Parse($"10.0.{i / 250}.{i % 250}"), out _) ? 1 : 0;
        }

        Assert.Equal(AgentRefusalBudget.MaxAddresses + 1, recorded);
        Assert.Equal(AgentRefusalBudget.MaxAddresses + 1, budget.Tracked(_node, "invalid_signature"));

        // Once their window ends, an address that comes back gets its entry, saying how many of the others were left out.
        _clock.Advance(AgentRefusalBudget.Window);
        Assert.True(budget.TryRecord(_node, "invalid_signature", IPAddress.Parse("10.9.9.9"), out _));
        Assert.True(budget.TryRecord(_node, "invalid_signature", IPAddress.Parse("10.0.0.0"), out _));
        Assert.True(budget.Tracked(_node, "invalid_signature") <= AgentRefusalBudget.MaxAddresses + 1);
    }

    [Fact]
    public void AnAddressPastTheLimitIsCountedInTheSharedEntry()
    {
        var budget = new AgentRefusalBudget(_clock);
        for (var i = 0; i < AgentRefusalBudget.MaxAddresses; i++)
        {
            Assert.True(budget.TryRecord(_node, "invalid_signature", IPAddress.Parse($"10.0.0.{i}"), out _));
        }

        Assert.True(budget.TryRecord(_node, "invalid_signature", IPAddress.Parse("10.0.1.1"), out _));
        Assert.False(budget.TryRecord(_node, "invalid_signature", IPAddress.Parse("10.0.1.2"), out _));
        Assert.False(budget.TryRecord(_node, "invalid_signature", IPAddress.Parse("10.0.1.3"), out _));

        _clock.Advance(AgentRefusalBudget.Window);
        // The addresses' entries expired: the next ones take their places again.
        Assert.True(budget.TryRecord(_node, "invalid_signature", IPAddress.Parse("10.0.1.2"), out _));
    }
}
