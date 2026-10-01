using System.Net.Http.Json;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;

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

    private static async Task<AuditPage> GetAsync(PanelClient client, string path) =>
        (await client.Http.GetFromJsonAsync<AuditPage>(new Uri(path, UriKind.Relative), AccessScenario.Json, TestContext.Current.CancellationToken))!;
}
