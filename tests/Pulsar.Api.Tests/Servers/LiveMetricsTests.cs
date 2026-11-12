using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Realtime;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Servers;

public sealed class LiveMetricsTests
{
    private static readonly NodeId s_node = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));

    [Fact]
    public void ASnapshotIsReturnedOnlyForTheNodeThatReportedIt()
    {
        var clock = new FakeTimeProvider();
        using var metrics = new LiveMetrics(clock);
        var server = ServerId.New();
        var otherNode = new NodeId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var first = Sample(server, clock);
        metrics.Update(s_node, first);
        Assert.Same(first, metrics.Find(server, s_node));
        Assert.Null(metrics.Find(server, otherNode));
        var moved = Sample(server, clock);
        metrics.Update(otherNode, moved);
        Assert.Null(metrics.Find(server, s_node));
        Assert.Same(moved, metrics.Find(server, otherNode));
    }

    [Fact]
    public void ExpiredSamplesAreReleasedWithoutAnotherReadOrStateReport()
    {
        var clock = new FakeTimeProvider();
        using var metrics = new LiveMetrics(clock);
        for (var i = 0; i < 3; i++)
        {
            metrics.Update(s_node, Sample(ServerId.New(), clock));
        }

        Assert.Equal(3, Retained(metrics).Count);
        clock.Advance(LiveMetrics.MaximumAge + LiveMetrics.MaximumAge);
        Assert.Empty(Retained(metrics));
    }

    [Fact]
    public void ExpirationPreservesFreshReplacementAndOtherFreshServers()
    {
        var clock = new FakeTimeProvider();
        using var metrics = new LiveMetrics(clock);
        var replaced = ServerId.New();
        var expired = ServerId.New();
        metrics.Update(s_node, Sample(replaced, clock));
        metrics.Update(s_node, Sample(expired, clock));
        clock.Advance(TimeSpan.FromSeconds(20));
        var replacement = Sample(replaced, clock);
        var other = Sample(ServerId.New(), clock);
        metrics.Update(s_node, replacement);
        metrics.Update(s_node, other);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Retained(metrics).Count);
        Assert.Same(replacement, metrics.Find(replaced));
        Assert.Same(other, metrics.Find(new ServerId(other.ServerId)));
        Assert.Null(metrics.Find(expired));
    }

    [Fact]
    public void ReadingAnExpiredSampleReleasesItBeforeTheNextSweep()
    {
        var clock = new FakeTimeProvider();
        using var metrics = new LiveMetrics(clock);
        var server = ServerId.New();
        metrics.Update(s_node, Sample(server, clock));
        clock.Advance(LiveMetrics.MaximumAge);
        clock.Advance(TimeSpan.FromTicks(1));

        Assert.Null(metrics.Find(server));
        Assert.Empty(Retained(metrics));
    }

    [Fact]
    public void DisposingTheCacheReleasesItsSamples()
    {
        var clock = new FakeTimeProvider();
        var metrics = new LiveMetrics(clock);
        metrics.Update(s_node, Sample(ServerId.New(), clock));
        metrics.Dispose();
        Assert.Empty(Retained(metrics));
        clock.Advance(LiveMetrics.MaximumAge + LiveMetrics.MaximumAge);
        Assert.Empty(Retained(metrics));
    }

    private static ServerMetricsView Sample(ServerId server, TimeProvider clock) =>
        new(server.Value, clock.GetUtcNow(), 1, 1024, 1, 1);

    // Find hides stale values; inspect retained data to distinguish eviction from hiding.
    private static ConcurrentDictionary<ServerId, LiveMetrics.Sample> Retained(LiveMetrics metrics) =>
        (ConcurrentDictionary<ServerId, LiveMetrics.Sample>)typeof(LiveMetrics)
            .GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(metrics)!;
}
