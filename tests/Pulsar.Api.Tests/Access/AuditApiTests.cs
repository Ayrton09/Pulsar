using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Access;

public sealed class AuditApiTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task EntriesArePagedNewestFirstWithoutGapsOrOverlaps()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        for (var i = 0; i < 3; i++)
        {
            await scenario.CreateRoleAsync($"Audit role {i}", Permissions.Server.View);
        }

        var all = await GetAsync(admin, "/api/v1/audit?limit=200");
        var first = await GetAsync(admin, "/api/v1/audit?limit=2");
        var second = await GetAsync(admin, $"/api/v1/audit?limit=2&before={first.NextBefore}");

        Assert.Equal(all.Entries.Take(4).Select(e => e.Id), first.Entries.Concat(second.Entries).Select(e => e.Id));
        Assert.Equal(all.Entries.OrderByDescending(e => e.OccurredAt).Select(e => e.Id), all.Entries.Select(e => e.Id));
    }

    [DatabaseFact]
    public async Task EntriesCanBeFilteredByActionPrefix()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateRoleAsync("Filtered role", Permissions.Server.View);

        var page = await GetAsync(admin, "/api/v1/audit?action=role.");

        Assert.NotEmpty(page.Entries);
        Assert.All(page.Entries, e => Assert.StartsWith("role.", e.Action, StringComparison.Ordinal));
        Assert.Contains(page.Entries, e => e.Action == AuditActions.Role.Created && e.ActorDisplay == PulsarApiFactory.AdminUserName);
    }

    [DatabaseFact]
    public async Task FiltersKeepTheirBoundsAndCursor()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var actor = UserId.New();
        var start = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var target = Guid.NewGuid().ToString();
        AuditEntry Make(int minute, string resource) => AuditEntry.Create(start.AddMinutes(minute), "server.renamed",
            AuditOutcome.Success, AuditActor.ForUser(actor, "filter-test"), AuditRequestInfo.None).WithTarget("server", resource);
        var first = Make(1, target);
        var second = Make(2, target);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            db.AuditLog.AddRange(Make(-1, target), first, second, Make(3, target), Make(1, "other"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var filter = $"action=server.&actorUserId={actor.Value}&from=2026-10-01T12:00:00Z&until=2026-10-01T12:03:00Z&targetType=server&targetId={target}";
        var page = await GetAsync(admin, $"/api/v1/audit?{filter}&limit=1");
        Assert.Equal(second.Id, Assert.Single(page.Entries).Id);
        var next = await GetAsync(admin, $"/api/v1/audit?{filter}&limit=1&before={page.NextBefore}");
        Assert.Equal(first.Id, Assert.Single(next.Entries).Id);
        Assert.Null(next.NextBefore);
        using var invalid = await admin.GetAsync("/api/v1/audit?from=2026-10-02T00:00:00Z&until=2026-10-01T00:00:00Z");
        await SetupTests.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation.failed");
    }

    [DatabaseFact]
    public async Task ResourceFiltersAlsoFindActionsRecordedThroughServerAndNodeContext()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var server = ServerId.New();
        var node = NodeId.New();
        var actor = UserId.New();
        var serverEntry = AuditEntry.Create(DateTimeOffset.UtcNow, AuditActions.Server.Renamed, AuditOutcome.Success,
            AuditActor.ForUser(actor, "context-test"), AuditRequestInfo.None).WithServer(server, node);
        var nodeEntry = AuditEntry.Create(DateTimeOffset.UtcNow, AuditActions.Node.Disabled, AuditOutcome.Success,
            AuditActor.ForUser(actor, "context-test"), AuditRequestInfo.None).WithNode(node);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            db.AuditLog.AddRange(serverEntry, nodeEntry);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var serverPage = await GetAsync(admin, $"/api/v1/audit?targetType=server&targetId={server.Value}&actorUserId={actor.Value}");
        Assert.Equal(serverEntry.Id, Assert.Single(serverPage.Entries).Id);
        Assert.Equal("server", serverPage.Entries[0].TargetType);
        Assert.Equal(server.Value.ToString(), serverPage.Entries[0].TargetId);
        var nodePage = await GetAsync(admin, $"/api/v1/audit?targetType=node&targetId={node.Value}&actorUserId={actor.Value}");
        Assert.Equal(nodeEntry.Id, Assert.Single(nodePage.Entries).Id);
        Assert.Equal("node", nodePage.Entries[0].TargetType);
    }

    private static async Task<AuditPage> GetAsync(PanelClient client, string path) =>
        (await client.Http.GetFromJsonAsync<AuditPage>(new Uri(path, UriKind.Relative), AccessScenario.Json, TestContext.Current.CancellationToken))!;
}
