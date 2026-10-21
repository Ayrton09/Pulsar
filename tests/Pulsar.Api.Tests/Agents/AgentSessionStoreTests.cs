using Microsoft.Extensions.Time.Testing;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>Agent sessions in memory: found by token or id, ended, and only a few at once per credential.</summary>
public sealed class AgentSessionStoreTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    [Fact]
    public void ACredentialHoldsOnlyItsNewestSessions()
    {
        var store = new AgentSessionStore(_clock);
        var node = new NodeId(Guid.NewGuid());
        var credential = Guid.NewGuid();
        var other = store.Create(node, Guid.NewGuid());

        // Sign-ins in a loop (a copied key, a stuck agent): the oldest go as new ones come; another credential's stay.
        var sessions = new List<(string Token, AgentSession Session)>();
        for (var i = 0; i < AgentSessionStore.MaxSessionsPerCredential + 3; i++)
        {
            sessions.Add(store.Create(node, credential));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var kept = sessions.TakeLast(AgentSessionStore.MaxSessionsPerCredential).ToList();
        Assert.All(kept, s => Assert.Same(s.Session, store.Find(s.Token)));
        Assert.All(kept, s => Assert.True(store.IsActive(s.Session.SessionId)));
        Assert.All(sessions.Except(kept), s => Assert.Null(store.Find(s.Token)));
        Assert.All(sessions.Except(kept), s => Assert.False(store.IsActive(s.Session.SessionId)));
        Assert.Same(other.Session, store.Find(other.Token));
    }

    [Fact]
    public void EndedAndExpiredSessionsAreFoundNoMore()
    {
        var store = new AgentSessionStore(_clock);
        var node = new NodeId(Guid.NewGuid());
        var ended = store.Create(node, Guid.NewGuid());
        var expiring = store.Create(node, Guid.NewGuid());

        store.End(ended.Session.SessionId);
        Assert.Null(store.Find(ended.Token));
        Assert.Null(store.Find(ended.Session.SessionId));

        _clock.Advance(AgentSessionStore.Lifetime);
        Assert.Null(store.Find(expiring.Session.SessionId));
        Assert.Null(store.Find(expiring.Token));
    }
}
