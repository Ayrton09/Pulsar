using Microsoft.Extensions.Time.Testing;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>How refused agent requests are held to one audit entry per node and reason a while (docs/architecture/03).</summary>
public sealed class AgentRefusalBudgetTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly NodeId _node = NodeId.New();

    [Fact]
    public void TheFirstRefusalIsRecordedAndTheSameOnesWithinTheWindowAreCounted()
    {
        var budget = new AgentRefusalBudget(_clock);

        Assert.True(budget.TryRecord(_node, "invalid_signature", out var first));
        Assert.Equal(0, first);
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False(budget.TryRecord(_node, "invalid_signature", out var leftOut));
            Assert.Equal(0, leftOut);
        }

        // The window counts from the recorded entry, not from the last refusal.
        _clock.Advance(AgentRefusalBudget.Window - TimeSpan.FromMinutes(3));
        Assert.True(budget.TryRecord(_node, "invalid_signature", out var notRecordedBefore));
        Assert.Equal(3, notRecordedBefore);

        // The count starts again with the new entry.
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(budget.TryRecord(_node, "invalid_signature", out _));
        _clock.Advance(AgentRefusalBudget.Window);
        Assert.True(budget.TryRecord(_node, "invalid_signature", out var again));
        Assert.Equal(1, again);
    }

    [Fact]
    public void JustBeforeTheWindowEndsTheRefusalIsStillCounted()
    {
        var budget = new AgentRefusalBudget(_clock);
        Assert.True(budget.TryRecord(_node, "invalid_signature", out _));

        _clock.Advance(AgentRefusalBudget.Window - TimeSpan.FromTicks(1));

        Assert.False(budget.TryRecord(_node, "invalid_signature", out _));
    }

    [Fact]
    public void EachNodeAndReasonHasItsOwnEntry()
    {
        var budget = new AgentRefusalBudget(_clock);
        var other = NodeId.New();

        Assert.True(budget.TryRecord(_node, "invalid_signature", out _));
        Assert.True(budget.TryRecord(_node, "credential_or_node_inactive", out _));
        Assert.True(budget.TryRecord(other, "invalid_signature", out _));
        Assert.False(budget.TryRecord(_node, "invalid_signature", out _));
        Assert.False(budget.TryRecord(other, "invalid_signature", out _));
    }
}
