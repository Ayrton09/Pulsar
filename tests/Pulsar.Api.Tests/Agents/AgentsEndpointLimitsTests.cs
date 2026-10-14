using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Panel;
using Pulsar.Api.Agents;
using Pulsar.Api.Http;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// What a connection to the agents' endpoint can cost the panel before it says who it is (docs/architecture/08, T31):
/// the chain of its certificate is built from what the handshake carried with the machine's trust, at the cost of a
/// chain without a policy, and connections at once are capped on that endpoint alone: those not signed in yet more
/// tightly, per address and network too, half of them for addresses agents sign in from, and for a while only; those
/// signed in per node, for as long as their session.
/// </summary>
public sealed class AgentsEndpointLimitsTests
{
    private static readonly TimeSpan s_deadline = new AgentsOptions().SignInDeadline;
    private static readonly NodeId s_node = new(Guid.NewGuid());
    private static readonly NodeId s_otherNode = new(Guid.NewGuid());

    [Fact]
    public void AClientCertificatesChainIsBuiltWithoutFetchingAnythingAndWithoutACustomTrustEngine()
    {
        var policy = AgentsEndpoint.ClientChainPolicy();

        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        // A custom root trust builds a chain engine of its own for every handshake: about 25 times the work, for a
        // chain whose result is ignored anyway.
        Assert.Equal(X509ChainTrustMode.System, policy.TrustMode);
    }

    [Fact]
    public async Task ConnectionsOverTheLimitAreClosedBeforeAnythingElse()
    {
        var limit = Limit(max: 3, pending: 2, perAddress: 2, out var clock);
        var test = new Connections(limit, clock);

        // An agent signs in from 10.0.0.1: its connection leaves the pending budget, and its address is an agent's now.
        test.Open("10.0.0.1", s_node);
        var pending = test.Open("10.0.0.1");
        test.Open("10.0.0.1");
        Assert.Equal(3, test.Served);
        test.Open("10.0.0.1");

        Assert.Equal(3, test.Served);
        Assert.Equal(3, limit.Open);
        await test.EndAsync(pending);
        Assert.Equal(2, limit.Open);
        test.Open("10.0.0.1");
        Assert.Equal(4, test.Served);
        await test.EndAllAsync();
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1", false)]
    [InlineData("10.0.0.1", "::ffff:10.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", "10.0.0.1", false)]
    [InlineData("10.0.0.1", "10.0.0.2", true)]
    [InlineData("2001:db8:0:1::1", "2001:db8:0:1:ffff::2", false)]
    [InlineData("2001:db8:0:1::1", "2001:db8:0:2::1", true)]
    [InlineData("10.0.0.1", "::a00:1", true)]
    [InlineData("127.0.0.1", "127.0.0.2", false)]
    [InlineData("127.0.0.1", "127.45.6.7", false)]
    [InlineData("127.0.0.1", "::1", false)]
    [InlineData("::ffff:127.0.0.9", "::1", false)]
    public async Task ConnectionsNotSignedInAreCappedPerAddress(string first, string second, bool accepted)
    {
        // IPv4 by address (mapped into IPv6 or not), IPv6 by /64, the panel's machine as one: what one host holds at the least.
        var limit = Limit(max: 10, pending: 10, perAddress: 1, out var clock, perNetwork: 10);
        var test = new Connections(limit, clock);

        test.Open(first);
        test.Open(second);

        Assert.Equal(accepted ? 2 : 1, test.Served);
        Assert.Equal(accepted ? 2 : 1, limit.Pending);
        await test.EndAllAsync();
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
        Assert.Equal(0, limit.Networks);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.200", false)]
    [InlineData("10.0.0.1", "10.0.1.1", true)]
    [InlineData("2001:db8:0:1::1", "2001:db8:0:ff00::1", false)]
    [InlineData("2001:db8:0:1::1", "2001:db8:1:1::1", true)]
    public async Task ConnectionsNotSignedInAreCappedPerNetwork(string first, string second, bool accepted)
    {
        // A host with several addresses, or a routed IPv6 prefix, gets the budget of its /24 or /48, not one per address.
        var limit = Limit(max: 10, pending: 10, perAddress: 1, out var clock, perNetwork: 1);
        var test = new Connections(limit, clock);

        test.Open(first);
        test.Open(second);

        Assert.Equal(accepted ? 2 : 1, test.Served);
        await test.EndAllAsync();
        Assert.Equal(0, limit.Networks);
    }

    [Fact]
    public async Task OneHostWithARoutedPrefixTakesOneNetworksShare()
    {
        // The defaults (1000, 250, 20 per address, 60 per network); one host with a /56: 256 /64s, one /48.
        var limit = new AgentsConnectionLimit(new AgentsOptions(), NullLogger.Instance, new FakeTimeProvider());
        var test = new Connections(limit, new FakeTimeProvider());
        var bytes = IPAddress.Parse("2001:db8:aaaa:bb00::").GetAddressBytes();
        for (var network = 0; network < 13; network++)
        {
            bytes[7] = (byte)network;
            for (var i = 0; i < 20; i++)
            {
                bytes[15] = (byte)(i + 1);
                test.Open(new IPAddress(bytes).ToString());
            }
        }

        Assert.Equal(60, limit.Pending);
        // An agent from elsewhere still gets in.
        test.Open("198.51.100.7");
        Assert.Equal(61, limit.Pending);
        await test.EndAllAsync();
        Assert.Equal(0, limit.Pending);
    }

    [Fact]
    public async Task HalfThePendingBudgetIsKeptForAddressesAgentsSignedInFrom()
    {
        var limit = Limit(max: 20, pending: 4, perAddress: 4, out var clock);
        var test = new Connections(limit, clock);
        // An agent signed in from 10.0.3.1 earlier today.
        await test.EndAsync(test.Open("10.0.3.1", s_node));
        Assert.Equal(1, limit.Known);

        // Strangers take two places at most, from any number of addresses.
        test.Open("10.0.0.1");
        test.Open("10.0.1.1");
        test.Open("10.0.2.1");
        Assert.Equal(2, limit.Pending);

        // The agent's address has the rest.
        test.Open("10.0.3.1");
        test.Open("10.0.3.1");
        Assert.Equal(4, limit.Pending);
        test.Open("10.0.3.1");
        Assert.Equal(4, limit.Pending);
        await test.EndAllAsync();

        // A day without a sign-in there, and it is a stranger's again.
        clock.Advance(AgentsConnectionLimit.KnownFor);
        test.Open("10.0.0.1");
        test.Open("10.0.1.1");
        test.Open("10.0.3.1");
        Assert.Equal(2, limit.Pending);
        await test.EndAllAsync();
    }

    [Fact]
    public async Task OneNodeKeyMakesAFewAddressesKnownAndCannotPushOthersOut()
    {
        var clock = new FakeTimeProvider();
        var limit = new AgentsConnectionLimit(new AgentsOptions(), NullLogger.Instance, clock);
        var test = new Connections(limit, clock);
        // 300 nodes signed in from their addresses over the day.
        for (var i = 0; i < 300; i++)
        {
            await test.EndAsync(test.Open($"10.8.{i / 250}.{(i % 250) + 1}", new NodeId(Guid.NewGuid())));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // One node key signing in from 4100 addresses (a routed /48) keeps the last few only.
        var mallory = new NodeId(Guid.NewGuid());
        for (var i = 0; i < 4100; i++)
        {
            await test.EndAsync(test.Open($"2001:db8:bad:{i:x}::1", mallory));
        }

        Assert.Equal(300 + AgentsConnectionLimit.KnownPerNode, limit.Known);

        // Strangers take their half; the agents' addresses are still theirs.
        FillStrangersHalf(test);
        Assert.Equal(125, limit.Pending);
        test.Open("10.8.0.7");
        Assert.Equal(126, limit.Pending);
        await test.EndAllAsync();
    }

    [Fact]
    public async Task OneNodeKeyCannotOpenTheWholeBudgetToStrangers()
    {
        var clock = new FakeTimeProvider();
        var limit = new AgentsConnectionLimit(new AgentsOptions(), NullLogger.Instance, clock);
        var test = new Connections(limit, clock);
        await test.EndAsync(test.Open("10.8.0.9", s_node));
        // Five /24 networks, each with addresses the key signed in from.
        var mallory = new NodeId(Guid.NewGuid());
        for (var network = 0; network < 5; network++)
        {
            for (var host = 1; host <= 3; host++)
            {
                await test.EndAsync(test.Open($"203.0.{network}.{host}", mallory));
            }
        }

        for (var network = 0; network < 5; network++)
        {
            for (var i = 0; i < 60; i++)
            {
                test.Open($"203.0.{network}.{1 + (i / 20)}");
            }
        }

        // Its few known addresses (20 places each) and the strangers' half: the agent still gets in.
        Assert.True(limit.Pending <= 125 + (AgentsConnectionLimit.KnownPerNode * 20), $"{limit.Pending} pending");
        var before = limit.Pending;
        test.Open("10.8.0.9");
        Assert.Equal(before + 1, limit.Pending);
        await test.EndAllAsync();
    }

    [Fact]
    public async Task PastTheBoundTheNodeWithTheMostAddressesLosesOne()
    {
        var clock = new FakeTimeProvider();
        var limit = new AgentsConnectionLimit(new AgentsOptions(), NullLogger.Instance, clock);
        var test = new Connections(limit, clock);
        await test.EndAsync(test.Open("10.8.0.7", s_node));
        clock.Advance(TimeSpan.FromSeconds(1));

        // 1100 more nodes with as many addresses as a node keeps: past 4096, the oldest of the fullest go.
        for (var i = 0; i < 1100; i++)
        {
            var node = new NodeId(Guid.NewGuid());
            for (var address = 0; address < AgentsConnectionLimit.KnownPerNode; address++)
            {
                await test.EndAsync(test.Open($"2001:db8:{i:x}:{address:x}::1", node));
            }
        }

        Assert.Equal(4096, limit.Known);
        FillStrangersHalf(test);
        test.Open("10.8.0.7");
        Assert.Equal(126, limit.Pending);
        await test.EndAllAsync();
    }

    [Fact]
    public async Task AKnownAgentIsNotStarvedByNeighboursOfItsNetwork()
    {
        foreach (var (agent, neighbour) in new[] { ("10.9.0.5", "10.9.0.{0}"), ("2001:db8:1:1::5", "2001:db8:1:{0}::7") })
        {
            var clock = new FakeTimeProvider();
            var limit = new AgentsConnectionLimit(new AgentsOptions(), NullLogger.Instance, clock);
            var test = new Connections(limit, clock);
            await test.EndAsync(test.Open(agent, s_node));

            // Three neighbours in the same /24 or /48 hold its 60 places for strangers; a fourth gets none.
            for (var host = 101; host <= 104; host++)
            {
                for (var i = 0; i < 20; i++)
                {
                    test.Open(string.Format(System.Globalization.CultureInfo.InvariantCulture, neighbour, host));
                }
            }

            Assert.Equal(60, limit.Pending);
            // The agent's address keeps its own 20.
            test.Open(agent);
            Assert.Equal(61, limit.Pending);
            await test.EndAllAsync();
            Assert.Equal(0, limit.Networks);
        }
    }

    [Fact]
    public async Task TransfersCannotTakeTheConnectionsANodeKeepsForItsHub()
    {
        var limit = Limit(max: 20, pending: 20, perAddress: 20, out var clock, perNode: 4);
        var test = new Connections(limit, clock);
        var ends = clock.GetUtcNow() + AgentSessionStore.Lifetime;
        DefaultConnectionContext Signed(bool transfer)
        {
            var connection = test.Open("10.0.0.1");
            connection.Items["request"] = connection.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_node, Guid.NewGuid(), ends, transfer);
            return connection;
        }

        // Of four, transfers take two; a hub, the hub it replaces and a rotation still fit.
        Assert.NotNull(Signed(transfer: true).Items["request"]);
        Assert.NotNull(Signed(transfer: true).Items["request"]);
        Assert.Null(Signed(transfer: true).Items["request"]);
        Assert.NotNull(Signed(transfer: false).Items["request"]);
        Assert.NotNull(Signed(transfer: false).Items["request"]);
        Assert.Null(Signed(transfer: false).Items["request"]);
        Assert.Equal(4, limit.SignedIn(s_node));
        await test.EndAllAsync();
    }

    [Fact]
    public async Task ARefusedNodeIsNamedInTheLog()
    {
        var logger = new CountingLogger();
        var clock = new FakeTimeProvider();
        var limit = new AgentsConnectionLimit(new AgentsOptions { MaxConnectionsPerNode = 1 }, logger, clock);
        var test = new Connections(limit, clock);
        test.Open("10.0.0.1", s_node);

        var refused = test.Open("10.0.0.1");
        Assert.Null(refused.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_node, Guid.NewGuid(), clock.GetUtcNow() + AgentSessionStore.Lifetime));

        Assert.Contains(s_node.Value.ToString(), Assert.Single(logger.Messages), StringComparison.Ordinal);
        await test.EndAllAsync();
    }

    /// <summary>Strangers (never signed in) fill the 125 places of their half from three /24 networks.</summary>
    private static void FillStrangersHalf(Connections test)
    {
        foreach (var (network, count) in new[] { (100, 60), (101, 60), (102, 5) })
        {
            for (var i = 0; i < count; i++)
            {
                test.Open($"198.51.{network}.{1 + (i / 20)}");
            }
        }
    }

    [Fact]
    public async Task ConnectionsNotSignedInHaveABudgetOfTheirOwnWhichSignedInOnesLeave()
    {
        var limit = Limit(max: 3, pending: 4, perAddress: 4, out var clock);
        var test = new Connections(limit, clock);

        var first = test.Open("10.0.0.1");
        test.Open("10.0.0.2");
        Assert.Equal(2, limit.Pending);
        // Unknown addresses have half the budget: full.
        test.Open("10.0.0.3");
        Assert.Equal(2, test.Served);

        // What the agents' authentication does with each request that presents a valid session.
        using (Authenticate(first, s_node, clock))
        using (Authenticate(first, s_node, clock))
        {
            Assert.Equal(1, limit.Pending);
            Assert.Equal(1, limit.Addresses);
            Assert.Equal(1, limit.SignedIn(s_node));
        }

        test.Open("10.0.0.1");
        Assert.Equal(3, test.Served);
        // The endpoint is full now: MaxConnections stays the outer bound.
        test.Open("10.0.0.1");
        Assert.Equal(3, test.Served);
        Assert.Equal(3, limit.Open);

        await test.EndAllAsync();
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
        Assert.Equal(0, limit.SignedIn(s_node));
    }

    [Fact]
    public async Task ANodeHoldsSoManyConnectionsAndEachConnectionBelongsToOneNode()
    {
        var limit = Limit(max: 10, pending: 10, perAddress: 10, out var clock, perNode: 2);
        var test = new Connections(limit, clock);

        test.Open("10.0.0.1", s_node);
        var second = test.Open("10.0.0.1", s_node);
        var third = test.Open("10.0.0.1");
        Assert.Null(third.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_node, Guid.NewGuid(), clock.GetUtcNow() + AgentSessionStore.Lifetime));
        Assert.Equal(2, limit.SignedIn(s_node));
        Assert.Equal(1, limit.Pending);

        // Another node is not counted with it, and a connection signed in for one node is not another's.
        Assert.NotNull(third.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_otherNode, Guid.NewGuid(), clock.GetUtcNow() + AgentSessionStore.Lifetime));
        Assert.Null(second.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_otherNode, Guid.NewGuid(), clock.GetUtcNow() + AgentSessionStore.Lifetime));
        Assert.Equal(0, limit.Pending);

        // A place freed is a place again.
        await test.EndAsync(second);
        Assert.NotNull(test.Open("10.0.0.1", s_node).Features.Get<AgentsEndpointConnection>());
        Assert.Equal(2, limit.SignedIn(s_node));
        await test.EndAllAsync();
        Assert.Equal(0, limit.SignedIn(s_node));
        Assert.Equal(0, limit.SignedIn(s_otherNode));
    }

    [Fact]
    public async Task ACountedConnectionIsReleasedWhenItFails()
    {
        var limit = Limit(max: 2, pending: 2, perAddress: 2, out _);
        var handler = limit.Wrap(_ => throw new InvalidOperationException("the connection broke"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(From("10.0.0.1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(From("2001:db8::1")));

        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
        Assert.Equal(0, limit.Networks);
    }

    [Fact]
    public async Task AConnectionThatDoesNotSignInIsClosedAtTheDeadline()
    {
        var limit = Limit(max: 10, pending: 10, perAddress: 10, out var clock);
        var test = new Connections(limit, clock);
        var idle = test.Open("10.0.0.1");
        var agent = test.Open("10.0.0.1", s_node);

        clock.Advance(s_deadline - TimeSpan.FromSeconds(1));
        Assert.False(Lifetime.Of(idle).CloseRequested);

        // First as Kestrel closes an idle keep-alive connection: once the request in progress, if any, is answered.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(Lifetime.Of(idle).CloseRequested);
        Assert.False(idle.ConnectionClosed.IsCancellationRequested);

        // Then whatever it is doing.
        clock.Advance(AgentsConnectionLimit.CloseGrace);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(idle.ConnectionClosed.IsCancellationRequested), "the idle connection aborted");

        // A signed-in connection is not timed out while its session lasts.
        clock.Advance(AgentSessionStore.Lifetime - s_deadline - AgentsConnectionLimit.CloseGrace - TimeSpan.FromSeconds(1));
        Assert.False(Lifetime.Of(agent).CloseRequested);
        Assert.False(agent.ConnectionClosed.IsCancellationRequested);
        Assert.Equal(1, limit.Pending);

        await test.EndAllAsync();
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
    }

    [Fact]
    public async Task AConnectionThatSignsInDuringItsGraceIsNotAborted()
    {
        var limit = Limit(max: 10, pending: 10, perAddress: 10, out var clock);
        var test = new Connections(limit, clock);
        var connection = test.Open("10.0.0.1");

        clock.Advance(s_deadline);
        Assert.True(Lifetime.Of(connection).CloseRequested);
        // A request on its way when the deadline came presented a session: it is answered, then the connection closes.
        using (Authenticate(connection, s_node, clock))
        {
            clock.Advance(AgentsConnectionLimit.CloseGrace + TimeSpan.FromMinutes(1));
        }

        Assert.False(connection.ConnectionClosed.IsCancellationRequested);
        await test.EndAllAsync();
        Assert.Equal(0, limit.Open);
    }

    [Fact]
    public async Task ASignedInConnectionLastsAsLongAsItsSessionAndAnyRequestOfOne()
    {
        var limit = Limit(max: 10, pending: 10, perAddress: 10, out var clock);
        var test = new Connections(limit, clock);
        var idle = test.Open("10.0.0.1", s_node);
        var transfer = test.Open("10.0.0.1");
        var transferRequest = Authenticate(transfer, s_node, clock);
        var renewed = test.Open("10.0.0.1", s_node);

        // A newer session presented on it before the end: it lasts as long as that one.
        clock.Advance(AgentSessionStore.Lifetime - TimeSpan.FromMinutes(1));
        Authenticate(renewed, s_node, clock).Dispose();

        // The session ends: asked to close, then aborted, unless a request of a session is in progress (a transfer).
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(Lifetime.Of(idle).CloseRequested);
        Assert.True(Lifetime.Of(transfer).CloseRequested);
        Assert.False(Lifetime.Of(renewed).CloseRequested);
        clock.Advance(AgentsConnectionLimit.CloseGrace);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(idle.ConnectionClosed.IsCancellationRequested), "the idle connection aborted");
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(transfer.ConnectionClosed.IsCancellationRequested);

        // Once the transfer is answered, the connection goes (Kestrel closes it at once; the abort is the backstop).
        transferRequest.Dispose();
        clock.Advance(AgentsConnectionLimit.CloseGrace);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(transfer.ConnectionClosed.IsCancellationRequested), "the transfer's connection aborted");
        Assert.False(renewed.ConnectionClosed.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(Lifetime.Of(renewed).CloseRequested);

        await test.EndAllAsync();
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.SignedIn(s_node));
    }

    [Fact]
    public async Task ANodesConnectionsWhoseSessionEndedAreClosedWithIt()
    {
        var limit = Limit(max: 10, pending: 10, perAddress: 10, out var clock);
        var test = new Connections(limit, clock);
        var ended = Guid.NewGuid();
        var active = Guid.NewGuid();
        var first = test.Open("10.0.0.1");
        var second = test.Open("10.0.0.1");
        var other = test.Open("10.0.0.2");
        var request = first.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_node, ended, clock.GetUtcNow() + AgentSessionStore.Lifetime);
        second.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_node, active, clock.GetUtcNow() + AgentSessionStore.Lifetime)!.Dispose();
        other.Features.Get<AgentsEndpointConnection>()!.Authenticated(s_otherNode, ended, clock.GetUtcNow() + AgentSessionStore.Lifetime)!.Dispose();

        // A revocation cuts even a request in progress.
        limit.CloseEnded(s_node, session => session == active);

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(first.ConnectionClosed.IsCancellationRequested), "the ended session's connection aborted");
        Assert.False(second.ConnectionClosed.IsCancellationRequested);
        Assert.False(other.ConnectionClosed.IsCancellationRequested);
        request!.Dispose();
        await test.EndAllAsync();
        Assert.Equal(0, limit.Open);
    }

    [Fact]
    public async Task RefusalsAndTimeoutsAreLoggedAtMostOnceAMinute()
    {
        var logger = new CountingLogger();
        var clock = new FakeTimeProvider();
        var limit = new AgentsConnectionLimit(new AgentsOptions { MaxConnections = 1, MaxPendingConnections = 1 }, logger, clock);
        var release = new TaskCompletionSource();
        var handler = limit.Wrap(_ => release.Task);
        var held = handler(From("10.0.0.1"));

        for (var i = 0; i < 5; i++)
        {
            await handler(From("10.0.0.2"));
        }

        Assert.Single(logger.Messages);
        clock.Advance(s_deadline);
        await handler(From("10.0.0.2"));
        Assert.Single(logger.Messages);

        // The held connection timed out meanwhile: the next entry has it and the refusals since the first.
        clock.Advance(TimeSpan.FromMinutes(1) - s_deadline);
        await handler(From("10.0.0.2"));
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("closed 6 connection(s) on arrival (6 over Agents:MaxConnections", logger.Messages[1], StringComparison.Ordinal);
        Assert.Contains("and closed 1 that", logger.Messages[1], StringComparison.Ordinal);

        release.SetResult();
        await held;
    }

    [Fact]
    public async Task CountersBalanceUnderConcurrentSignInsClosesAndTimeouts()
    {
        var options = new AgentsOptions
        {
            MaxConnections = 400,
            MaxPendingConnections = 150,
            MaxPendingConnectionsPerAddress = 12,
            MaxConnectionsPerNode = 5,
            SignInDeadline = TimeSpan.FromMilliseconds(15),
        };
        var limit = new AgentsConnectionLimit(options, NullLogger.Instance, TimeProvider.System);
        var nodes = Enumerable.Range(0, 4).Select(_ => new NodeId(Guid.NewGuid())).ToArray();
        var handler = limit.Wrap(async connection =>
        {
            var random = Random.Shared;
            var counted = connection.Features.Get<AgentsEndpointConnection>()!;
            var node = nodes[random.Next(nodes.Length)];
            switch (random.Next(6))
            {
                case 0:
                    throw new InvalidOperationException("broke");
                case 1:
                    var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                        Task.Run(() => counted.Authenticated(node, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMilliseconds(random.Next(1, 30))))));
                    await Task.Delay(random.Next(0, 30));
                    foreach (var request in requests)
                    {
                        request?.Dispose();
                    }

                    break;
                case 2:
                    await Task.Delay(random.Next(0, 40));
                    counted.Authenticated(node, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1))?.Dispose();
                    break;
                case 3:
                    await Task.Delay(random.Next(10, 25));
                    break;
                case 4:
                    counted.Authenticated(node, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1))?.Dispose();
                    limit.CloseEnded(node, _ => random.Next(2) == 0);
                    await Task.Delay(random.Next(0, 10));
                    break;
                default:
                    break;
            }
        });

        var tasks = new List<Task>();
        for (var i = 0; i < 20000; i++)
        {
            var context = From($"10.0.{i % 7}.{i % 5}", 40000 + (i % 1000));
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await handler(context);
                }
                catch (InvalidOperationException)
                {
                }
            }, TestContext.Current.CancellationToken));
            if (i % 200 == 0)
            {
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        }

        await Task.WhenAll(tasks);
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
        Assert.Equal(0, limit.Networks);
        Assert.All(nodes, node => Assert.Equal(0, limit.SignedIn(node)));
    }

    [Theory]
    [InlineData("2001:db8:0:1::1", "2001:db8:0:1:ffff::2", true)]
    [InlineData("2001:db8:0:1::1", "2001:db8:0:2::1", false)]
    [InlineData("10.0.0.1", "::ffff:10.0.0.1", true)]
    [InlineData("10.0.0.1", "10.0.0.2", false)]
    [InlineData("127.0.0.1", "::1", true)]
    [InlineData("127.0.0.1", "127.0.0.2", true)]
    public void RateLimitsCountAnIPv6NetworkAndTheLoopbackAsOneClient(string first, string second, bool same)
    {
        var a = ClientAddress.PartitionOf(IPAddress.Parse(first));
        var b = ClientAddress.PartitionOf(IPAddress.Parse(second));

        Assert.Equal(same, a == b);
        Assert.Equal("unknown", ClientAddress.PartitionOf(null));
    }

    [Theory]
    [InlineData("0", null, null, null, null)]
    [InlineData(null, "0", null, null, null)]
    [InlineData(null, null, "0", null, null)]
    [InlineData("10", "11", null, null, null)]
    [InlineData(null, "8", "9", null, null)]
    [InlineData(null, null, "20", "19", null)]
    [InlineData(null, "100", null, "101", null)]
    [InlineData(null, null, null, null, "0")]
    [InlineData("10", null, null, null, "11")]
    public void BudgetsAreCheckedAtStartup(string? max, string? pending, string? perAddress, string? perNetwork, string? perNode)
    {
        using var services = Services(new Dictionary<string, string?>
        {
            ["Agents:MaxConnections"] = max,
            ["Agents:MaxPendingConnections"] = pending,
            ["Agents:MaxPendingConnectionsPerAddress"] = perAddress,
            ["Agents:MaxPendingConnectionsPerNetwork"] = perNetwork,
            ["Agents:MaxConnectionsPerNode"] = perNode,
        });

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<AgentsOptions>>().Value);
    }

    [Fact]
    public void BudgetsNotSetFollowTheEndpointsCap()
    {
        using (var services = Services([]))
        {
            var options = services.GetRequiredService<IOptions<AgentsOptions>>().Value;
            Assert.Equal(1000, options.MaxConnections);
            Assert.Equal(250, options.PendingLimit);
            Assert.Equal(20, options.PendingPerAddressLimit);
            Assert.Equal(60, options.PendingPerNetworkLimit);
            Assert.Equal(16, options.PerNodeLimit);
        }

        using (var services = Services(new Dictionary<string, string?> { ["Agents:MaxConnections"] = "40" }))
        {
            var options = services.GetRequiredService<IOptions<AgentsOptions>>().Value;
            Assert.Equal(10, options.PendingLimit);
            Assert.Equal(10, options.PendingPerAddressLimit);
            Assert.Equal(10, options.PendingPerNetworkLimit);
            Assert.Equal(16, options.PerNodeLimit);
        }

        using (var services = Services(new Dictionary<string, string?> { ["Agents:MaxConnections"] = "8" }))
        {
            Assert.Equal(8, services.GetRequiredService<IOptions<AgentsOptions>>().Value.PerNodeLimit);
        }
    }

    private static IDisposable Authenticate(DefaultConnectionContext connection, NodeId node, FakeTimeProvider clock) =>
        connection.Features.Get<AgentsEndpointConnection>()!.Authenticated(node, Guid.NewGuid(), clock.GetUtcNow() + AgentSessionStore.Lifetime)!;

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddPulsarApi(configuration).BuildServiceProvider();
    }

    private static AgentsConnectionLimit Limit(int max, int pending, int perAddress, out FakeTimeProvider clock, int? perNetwork = null,
        int? perNode = null)
    {
        clock = new FakeTimeProvider();
        return new AgentsConnectionLimit(
            new AgentsOptions
            {
                MaxConnections = max,
                MaxPendingConnections = pending,
                MaxPendingConnectionsPerAddress = perAddress,
                MaxPendingConnectionsPerNetwork = perNetwork,
                MaxConnectionsPerNode = perNode,
            },
            NullLogger.Instance, clock);
    }

    private static DefaultConnectionContext From(string address, int port = 50000)
    {
        var connection = new DefaultConnectionContext { RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), port) };
        connection.Features.Set<IConnectionLifetimeNotificationFeature>(new Lifetime());
        return connection;
    }

    /// <summary>Connections through the limit, held until they are ended; some signed in for a node at once.</summary>
    private sealed class Connections(AgentsConnectionLimit limit, FakeTimeProvider clock)
    {
        private readonly Dictionary<DefaultConnectionContext, (Task Served, TaskCompletionSource End)> _held = [];
        private readonly ConnectionDelegate _handler = limit.Wrap(async connection =>
        {
            var end = (TaskCompletionSource)connection.Items["end"]!;
            if (connection.Items.TryGetValue("node", out var node))
            {
                // A request that presented a session and was answered: the connection stays signed in.
                connection.Features.Get<AgentsEndpointConnection>()!
                    .Authenticated((NodeId)node!, Guid.NewGuid(), clock.GetUtcNow() + AgentSessionStore.Lifetime)!.Dispose();
            }

            connection.Items["served"] = true;
            await end.Task;
        });

        public int Served => _held.Keys.Count(c => c.Items.ContainsKey("served"));

        public DefaultConnectionContext Open(string address, NodeId? node = null)
        {
            var connection = From(address);
            var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Items["end"] = end;
            if (node is { } id)
            {
                connection.Items["node"] = id;
            }

            _held[connection] = (_handler(connection), end);
            return connection;
        }

        public async Task EndAsync(DefaultConnectionContext connection)
        {
            var (served, end) = _held[connection];
            end.TrySetResult();
            await served;
        }

        public async Task EndAllAsync()
        {
            foreach (var (_, end) in _held.Values)
            {
                end.TrySetResult();
            }

            await Task.WhenAll(_held.Values.Select(h => h.Served));
        }
    }

    private sealed class Lifetime : IConnectionLifetimeNotificationFeature
    {
        public CancellationToken ConnectionClosedRequested { get; set; }

        public bool CloseRequested { get; private set; }

        public static Lifetime Of(DefaultConnectionContext connection) => (Lifetime)connection.Features.Get<IConnectionLifetimeNotificationFeature>()!;

        public void RequestClose() => CloseRequested = true;
    }

    private sealed class CountingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

/// <summary>The agents' endpoint on real Kestrel with room for two connections at once from addresses no agent signed in from.</summary>
public sealed class AgentsEndpointConnectionLimitTests(AgentsEndpointConnectionLimitTests.SmallEndpointPanel factory)
    : IClassFixture<AgentsEndpointConnectionLimitTests.SmallEndpointPanel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ATlsHandshakeWaitsForAConnectionToEnd()
    {
        factory.StartServer();
        var agents = new IPEndPoint(IPAddress.Loopback, factory.AgentsPort);

        // Two connections that never start their handshake hold the strangers' share; the third is closed at once, unread.
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(agents, Ct);
        await second.ConnectAsync(agents, Ct);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Pending == 2), "both connections counted");
        using (var third = new TcpClient())
        {
            await third.ConnectAsync(agents, Ct);
            var read = await ReadAsync(third.GetStream());
            Assert.Equal(0, read);
        }

        // The main endpoint is not counted.
        using (var main = new HttpClient(factory.Handler()) { BaseAddress = factory.MainUrl })
        {
            Assert.Equal(HttpStatusCode.OK, (await main.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct)).StatusCode);
        }

        first.Dispose();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = KestrelPanelFactory.UsableCertificate(key);
        using var http = new HttpClient(factory.Handler(certificate)) { BaseAddress = factory.AgentsUrl };
        await InProcessAgent.EventuallyAsync(async () =>
        {
            try
            {
                using var response = await http.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct);
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }, "a connection once one ended");
    }

    [DatabaseFact]
    public async Task AConnectionThatNeverStartsItsHandshakeIsDroppedAfterFiveSeconds()
    {
        factory.StartServer();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(new IPEndPoint(IPAddress.Loopback, factory.AgentsPort), Ct);
        var watch = Stopwatch.StartNew();

        // Kestrel's own timeout would be 10 s; the sign-in deadline here is 30 s.
        var read = await ReadAsync(tcp.GetStream(), TimeSpan.FromSeconds(20));

        Assert.Equal(0, read);
        Assert.InRange(watch.Elapsed, AgentsEndpoint.HandshakeTimeout - TimeSpan.FromSeconds(1), AgentsEndpoint.HandshakeTimeout + TimeSpan.FromSeconds(4));
    }

    /// <summary>What the panel sends on a connection within <paramref name="within"/> (2 s): 0 when it closed it, -1 when it is still open.</summary>
    internal static async Task<int> ReadAsync(Stream stream, TimeSpan? within = null)
    {
        using var wait = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(2));
        try
        {
            return await stream.ReadAsync(new byte[16], wait.Token);
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public sealed class SmallEndpointPanel : KestrelPanelFactory
    {
        internal AgentsConnectionLimit Limit => Services.GetRequiredService<AgentsEndpoint>().ConnectionLimit;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Agents:MaxConnections", "4");
            builder.UseSetting("Agents:MaxPendingConnections", "4");
        }
    }
}

/// <summary>
/// The agents' endpoint on real Kestrel with room for four connections not signed in (two from addresses no agent signed
/// in from), which must sign in within a second.
/// </summary>
public sealed class AgentsEndpointSignInDeadlineTests(AgentsEndpointSignInDeadlineTests.ShortDeadlinePanel factory)
    : IClassFixture<AgentsEndpointSignInDeadlineTests.ShortDeadlinePanel>
{
    private static readonly TimeSpan s_deadline = TimeSpan.FromSeconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AConnectionIdleAfterItsHandshakeIsClosedAtTheDeadline()
    {
        factory.StartServer();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = KestrelPanelFactory.UsableCertificate(key);
        using var tcp = new TcpClient();
        var watch = Stopwatch.StartNew();
        await tcp.ConnectAsync(new IPEndPoint(IPAddress.Loopback, factory.AgentsPort), Ct);
        await using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = (_, server, _, _) => factory.IsPanel(server),
            ClientCertificates = [certificate],
            LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
        }, Ct);

        // Closed as an idle keep-alive connection is, at the deadline: before the grace a request in progress gets ends.
        var read = await AgentsEndpointConnectionLimitTests.ReadAsync(tls, s_deadline + AgentsConnectionLimit.CloseGrace - TimeSpan.FromSeconds(0.5));

        Assert.True(read == 0, read < 0
            ? $"still open {watch.Elapsed} after it arrived: it was not asked to close at the deadline"
            : $"the panel sent {read} byte(s) instead of closing the connection");
        Assert.True(watch.Elapsed >= s_deadline - TimeSpan.FromMilliseconds(100), $"closed after {watch.Elapsed}");
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Open == 0 && factory.Limit.Pending == 0 && factory.Limit.Addresses == 0),
            "the endpoint's counters back to zero");
    }

    [DatabaseFact]
    public async Task AnAgentsHubOutlivesTheDeadlineAndLeavesTheBudgetOfConnectionsNotSignedIn()
    {
        factory.StartServer();
        var created = await factory.CreateNodeAsync("deadline-agent");
        await using (var agent = new InProcessAgent(factory, transport: new TrustingTransport(factory)))
        {
            var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint, factory.MainUrl.ToString());
            var node = new NodeId(config.NodeId);
            await agent.StartAsync();
            await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == factory.AgentsUrl && agent.CurrentSessionId is not null),
                "the agent on the agents' address");
            var session = agent.CurrentSessionId;

            await Task.Delay(s_deadline + AgentsConnectionLimit.CloseGrace + TimeSpan.FromSeconds(1), Ct);

            // The same session on the same hub connection: it was never closed.
            Assert.Equal(session, agent.CurrentSessionId);
            var ping = await factory.Services.GetRequiredService<IAgentConnections>().SendAsync(node, new PingCommand("after the deadline"), null, Ct);
            Assert.Equal(AckStatus.Accepted, ping?.Status);
            Assert.True(factory.Limit.SignedIn(node) >= 1);
            // Its sign-in connections went with the deadline at the latest.
            await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Pending == 0), "no connection waiting to sign in");

            // The agent signed in from here, so the whole budget is open to this address, and the hub holds none of it.
            var agents = new IPEndPoint(IPAddress.Loopback, factory.AgentsPort);
            var idle = new List<TcpClient>();
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var client = new TcpClient();
                    idle.Add(client);
                    await client.ConnectAsync(agents, Ct);
                }

                await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Pending == 4), "the idle connections counted");
                using var refused = new TcpClient();
                await refused.ConnectAsync(agents, Ct);
                Assert.Equal(0, await AgentsEndpointConnectionLimitTests.ReadAsync(refused.GetStream()));
            }
            finally
            {
                idle.ForEach(c => c.Dispose());
            }
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Open == 0 && factory.Limit.Pending == 0 && factory.Limit.Addresses == 0),
            "the endpoint's counters back to zero");
    }

    /// <summary>The agent's real transport (sockets, TLS, WebSockets), trusting the test panel's certificate.</summary>
    private sealed class TrustingTransport(KestrelPanelFactory panel) : PanelTransport
    {
        public override HttpMessageHandler CreateHttpHandler() => panel.Handler();
    }

    public sealed class ShortDeadlinePanel : KestrelPanelFactory
    {
        internal AgentsConnectionLimit Limit => Services.GetRequiredService<AgentsEndpoint>().ConnectionLimit;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Agents:MaxPendingConnections", "4");
            builder.ConfigureTestServices(services => services.Configure<AgentsOptions>(o => o.SignInDeadline = s_deadline));
        }
    }
}
