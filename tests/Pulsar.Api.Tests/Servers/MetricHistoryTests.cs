using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Metrics;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Metrics;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Aggregated usage history: minute buckets, the hour roll-up, retention and the charts' API (Phase 13).</summary>
public sealed class MetricHistoryTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    // A fixed past hour: pruning with this clock removes nothing other tests wrote.
    private static readonly DateTimeOffset s_start = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task SamplesBecomeMinutesThenHoursAndAreKeptAsConfigured()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "buckets");
        var server = new ServerId(await InstalledServerAsync(admin, agent, "history-buckets"));
        var node = new NodeId(agent.Config!.NodeId);
        var clock = new FakeTimeProvider(s_start.AddSeconds(5));
        var history = NewHistory(clock);

        // 10:00: two samples, one with players.
        history.RecordServer(server, 10, 100, players: 2);
        history.RecordServer(server, 30, 300, players: null);
        history.RecordNode(node, 40, 1_000, 8_000);
        clock.Advance(TimeSpan.FromMinutes(1));
        // 10:01, still in progress: not written yet.
        history.RecordServer(server, 50, 500, players: 4);
        history.RecordNode(node, 60, 3_000, 8_000);
        await history.FlushAsync(includeCurrentMinute: false, Ct);

        var hour = await history.ReadServerAsync(server, MetricRange.Hour, Ct);
        Assert.Equal(60, hour.StepSeconds);
        var first = Assert.Single(hour.Points);
        Assert.Equal(s_start, first.At);
        Assert.Equal(20, first.CpuAverage, 3);
        Assert.Equal(30, first.CpuPeak, 3);
        Assert.Equal(200, first.MemoryAverage);
        Assert.Equal(300, first.MemoryPeak);
        Assert.Equal(2, first.PlayersAverage);
        Assert.Equal(2, first.PlayersPeak);

        // A panel stopping writes the minute in progress; one starting again within it adds to the same bucket.
        await history.FlushAsync(includeCurrentMinute: true, Ct);
        history.RecordServer(server, 70, 700, players: 6);
        await history.FlushAsync(includeCurrentMinute: true, Ct);
        hour = await history.ReadServerAsync(server, MetricRange.Hour, Ct);
        Assert.Equal([s_start, s_start.AddMinutes(1)], hour.Points.Select(p => p.At));
        var second = hour.Points[1];
        Assert.Equal(60, second.CpuAverage, 3);
        Assert.Equal(70, second.CpuPeak, 3);
        Assert.Equal(600, second.MemoryAverage);
        Assert.Equal(5, second.PlayersAverage);
        Assert.Equal(6, second.PlayersPeak);

        // The 24-hour chart groups minutes by five.
        var day = await history.ReadServerAsync(server, MetricRange.Day, Ct);
        Assert.Equal(300, day.StepSeconds);
        var five = Assert.Single(day.Points);
        Assert.Equal(s_start, five.At);
        Assert.Equal((10 + 30 + 50 + 70) / 4.0, five.CpuAverage, 3);
        Assert.Equal(70, five.CpuPeak, 3);
        Assert.Equal(4, five.PlayersAverage); // (2 + 4 + 6) / 3 samples with players
        Assert.Equal(6, five.PlayersPeak);

        // The roll-up makes the hour the long charts read; running it again replaces it rather than adding.
        await history.RollUpAsync(Ct);
        await history.RollUpAsync(Ct);
        var week = await history.ReadServerAsync(server, MetricRange.Week, Ct);
        Assert.Equal(3600, week.StepSeconds);
        var rolled = Assert.Single(week.Points);
        Assert.Equal(s_start, rolled.At);
        Assert.Equal(five.CpuAverage, rolled.CpuAverage, 3);
        Assert.Equal(five.MemoryAverage, rolled.MemoryAverage);
        Assert.Equal(4, rolled.PlayersAverage);
        var month = await history.ReadServerAsync(server, MetricRange.Month, Ct);
        Assert.Equal(s_start.AddHours(-2), Assert.Single(month.Points).At); // four-hour points from 08:00

        var nodeHour = await history.ReadNodeAsync(node, MetricRange.Hour, Ct);
        Assert.Equal([40.0, 60.0], nodeHour.Points.Select(p => p.CpuAverage));
        Assert.Equal([1_000L, 3_000L], nodeHour.Points.Select(p => p.MemoryUsedAverage));
        Assert.All(nodeHour.Points, p => Assert.Equal(8_000, p.MemoryTotal));
        var nodeWeek = Assert.Single((await history.ReadNodeAsync(node, MetricRange.Week, Ct)).Points);
        Assert.Equal(50, nodeWeek.CpuAverage, 3);
        Assert.Equal(3_000, nodeWeek.MemoryUsedPeak);

        // Three days later the minutes are gone (2 days kept) and the hour is still there (90 days).
        clock.Advance(TimeSpan.FromDays(3));
        await history.PruneAsync(Ct);
        var reader = NewHistory(new FakeTimeProvider(s_start.AddMinutes(30)));
        Assert.Empty((await reader.ReadServerAsync(server, MetricRange.Hour, Ct)).Points);
        Assert.Single((await reader.ReadServerAsync(server, MetricRange.Week, Ct)).Points);
        Assert.Empty((await reader.ReadNodeAsync(node, MetricRange.Hour, Ct)).Points);
    }

    [DatabaseFact]
    public async Task HistoryOfServersThatAreGoneIsDroppedAndAFailedWriteIsKeptForTheNext()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "deleted");
        var server = await InstalledServerAsync(admin, agent, "history-deleted");
        var clock = new FakeTimeProvider(s_start.AddHours(5));
        var history = NewHistory(clock);

        // Samples of a server nobody knows are dropped, not a failed write.
        history.RecordServer(new ServerId(Guid.CreateVersion7()), 10, 10, null);
        history.RecordServer(new ServerId(server), 10, 10, null);
        await history.FlushAsync(includeCurrentMinute: true, Ct);
        Assert.Single((await history.ReadServerAsync(new ServerId(server), MetricRange.Hour, Ct)).Points);

        // Deleting the server deletes its history.
        using (var deleted = await admin.DeleteAsync($"/api/v1/servers/{server}"))
        {
            await AccessScenario.EnsureSuccessAsync(deleted);
        }

        await InProcessAgent.EventuallyAsync(async () => await ServerApi.GetAsync(admin, server) is null, "server deleted");
        Assert.Empty((await history.ReadServerAsync(new ServerId(server), MetricRange.Hour, Ct)).Points);

        // A write that fails (cancelled here) keeps its buckets for the next one.
        var other = new ServerId(await InstalledServerAsync(admin, agent, "history-retry"));
        history.RecordServer(other, 25, 250, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.FlushAsync(includeCurrentMinute: true, new CancellationToken(canceled: true)));
        await history.FlushAsync(includeCurrentMinute: true, Ct);
        Assert.Equal(25, Assert.Single((await history.ReadServerAsync(other, MetricRange.Hour, Ct)).Points).CpuAverage, 3);
    }

    [DatabaseFact]
    public async Task AWriteKeepsAllItsBucketsOrNone()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "atomic");
        var server = new ServerId(await InstalledServerAsync(admin, agent, "history-atomic"));
        var node = new NodeId(agent.Config!.NodeId);
        var minute = s_start.AddHours(10);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetricHistoryStore>();
        var full = new NodeMetricBucket(node, MetricResolution.Minute, minute) { Samples = 1, MemoryUsedSum = long.MaxValue, MemoryUsedMax = 1, MemoryTotal = 1 };
        await store.AddAsync([], [full], Ct);

        // The node's statement fails after the server's ran (its sum leaves bigint): the server's samples are not kept
        // either, so the retry that follows adds them once.
        var bucket = new ServerMetricBucket(server, MetricResolution.Minute, minute) { Samples = 1, CpuSum = 10, CpuMax = 10, MemorySum = 100, MemoryMax = 100 };
        await Assert.ThrowsAnyAsync<DbException>(() => store.AddAsync([bucket], [full], Ct));
        await store.AddAsync([bucket], [], Ct);

        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT samples AS "Value" FROM server_metrics WHERE server_id = {server.Value} AND resolution = 1
            """).SingleAsync(Ct));
    }

    [DatabaseFact]
    public async Task MinutesWrittenBeforeAStopReachTheirHourAfterTheNextStart()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "restart");
        var server = new ServerId(await InstalledServerAsync(admin, agent, "history-restart"));
        var stopped = NewHistory(new FakeTimeProvider(s_start.AddHours(2).AddSeconds(5)));
        stopped.RecordServer(server, 40, 400, null);
        await stopped.FlushAsync(includeCurrentMinute: true, Ct);

        // The panel starts again five hours later: its first roll-up reaches back past the usual three hours.
        var started = NewHistory(new FakeTimeProvider(s_start.AddHours(7)));
        await started.RollUpAsync(Ct);
        var hour = Assert.Single((await started.ReadServerAsync(server, MetricRange.Week, Ct)).Points);
        Assert.Equal(s_start.AddHours(2), hour.At);
        Assert.Equal(40, hour.CpuAverage, 3);
    }

    [DatabaseFact]
    public async Task ImpossibleMemoryIsCappedRatherThanBreakingTheWrites()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "huge");
        var server = new ServerId(await InstalledServerAsync(admin, agent, "history-huge"));
        var node = new NodeId(agent.Config!.NodeId);
        var history = NewHistory(new FakeTimeProvider(s_start.AddHours(11).AddSeconds(5)));

        // A buggy or hostile agent: two samples of the same minute, written apart (merged by the database).
        for (var i = 0; i < 2; i++)
        {
            history.RecordServer(server, 10, long.MaxValue, null);
            history.RecordServer(server, 10, long.MaxValue, null);
            history.RecordNode(node, 10, long.MaxValue, long.MaxValue);
            await history.FlushAsync(includeCurrentMinute: true, Ct);
        }

        await history.RollUpAsync(Ct);
        var point = Assert.Single((await history.ReadServerAsync(server, MetricRange.Hour, Ct)).Points);
        Assert.Equal(MetricHistory.MaxMemoryBytes, point.MemoryAverage);
        Assert.Equal(MetricHistory.MaxMemoryBytes, point.MemoryPeak);
        var nodePoint = Assert.Single((await history.ReadNodeAsync(node, MetricRange.Week, Ct)).Points);
        Assert.Equal(MetricHistory.MaxMemoryBytes, nodePoint.MemoryUsedPeak);
        Assert.Equal(MetricHistory.MaxMemoryBytes, nodePoint.MemoryTotal);

        // A flood of samples: a minute keeps as many as it may.
        var flooded = new ServerId(await InstalledServerAsync(admin, agent, "history-flood"));
        for (var i = 0; i < MetricHistory.MaxSamplesPerMinute + 100; i++)
        {
            history.RecordServer(flooded, 10, long.MaxValue, null);
        }

        await history.FlushAsync(includeCurrentMinute: true, Ct);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(MetricHistory.MaxSamplesPerMinute, await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Database.SqlQuery<int>($"""
            SELECT samples AS "Value" FROM server_metrics WHERE server_id = {flooded.Value} AND resolution = 1
            """).SingleAsync(Ct));
    }

    [DatabaseFact]
    public async Task ARunningServersUsageReachesItsChartForThoseWhoMaySeeIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        var admin = scenario.Admin;
        await using var agent = await ConnectedAgentAsync(admin, "live");
        var server = await InstalledServerAsync(admin, agent, "history-live");
        Assert.Equal(AckStatus.Accepted, (await ServerApi.CommandAsync(admin, server, "start")).Status);
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");

        // The in-process agent reports usage every 200 ms; the panel's own history writes it out.
        var history = factory.Services.GetRequiredService<MetricHistory>();
        ServerMetricHistory? chart = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await history.FlushAsync(includeCurrentMinute: true, Ct);
            chart = await admin.Http.GetFromJsonAsync<ServerMetricHistory>($"/api/v1/servers/{server}/metrics?range=hour", AccessScenario.Json, Ct);
            return chart!.Points.Count > 0;
        }, "usage in the chart");
        Assert.Equal(MetricRange.Hour, chart!.Range);
        Assert.True(chart.Points[^1].MemoryPeak > 0);

        var nodeChart = await admin.Http.GetFromJsonAsync<NodeMetricHistory>($"/api/v1/nodes/{agent.Config!.NodeId}/metrics?range=day", AccessScenario.Json, Ct);
        Assert.Equal(300, nodeChart!.StepSeconds);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Http.GetAsync($"/api/v1/servers/{server}/metrics?range=year", Ct)).StatusCode);
        using (var missing = await admin.Http.GetAsync($"/api/v1/servers/{Guid.CreateVersion7()}/metrics", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("server.not_found", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var missing = await admin.Http.GetAsync($"/api/v1/nodes/{Guid.CreateVersion7()}/metrics", Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("node.not_found", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // Seeing the server is what reading its chart takes.
        using var stranger = await scenario.SignedInWithAsync("history-stranger");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.Http.GetAsync($"/api/v1/servers/{server}/metrics", Ct)).StatusCode);
        using var viewer = await scenario.SignedInWithAsync("history-viewer", Permissions.Server.View);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Http.GetAsync($"/api/v1/servers/{server}/metrics?range=week", Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await viewer.Http.GetAsync($"/api/v1/nodes/{agent.Config.NodeId}/metrics", Ct)).StatusCode);
    }

    private MetricHistory NewHistory(TimeProvider clock) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), clock, Options.Create(new MetricHistoryOptions()));

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"history-{name}");
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private static async Task<Guid> InstalledServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }
}
