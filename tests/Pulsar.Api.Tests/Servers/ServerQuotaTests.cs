using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Quotas (Pulsar 1.0.24): users create servers for themselves on the nodes their quota lists, with caps it counts, and
/// own them; who gives a quota on a node must be able to create servers there.
/// </summary>
public sealed class ServerQuotaTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AUserCreatesAndOwnsServersWithinTheirQuotaAndNoMore()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "quota");
        var node = agent.Config!.NodeId;
        await GameServerTests.AllocateAsync(admin, node, "0.0.0.0", "27160-27199");
        var player = await scenario.CreateUserAsync("quota-player");
        using var client = await scenario.SignInAsync("quota-player");
        var css = (await GameServerTests.TemplateAsync(admin, "css")).Template.Id;

        // Without a quota there is nothing to create servers with.
        await ProblemAsync(await client.GetAsync("/api/v1/account/quota"), HttpStatusCode.NotFound, AccessErrors.QuotaNotFound.Code);
        await ProblemAsync(await client.PostAsync("/api/v1/account/servers", new CreateOwnedServerRequest(node, css, "Mine", 1024, 50)),
            HttpStatusCode.NotFound, AccessErrors.QuotaNotFound.Code);

        using (var set = await admin.PutAsync($"/api/v1/users/{player.Id}/quota", new SetUserQuotaRequest(2, 2048, 100, [node])))
        {
            await AccessScenario.EnsureSuccessAsync(set);
        }

        var quota = (await client.Http.GetFromJsonAsync<QuotaView>(new Uri("/api/v1/account/quota", UriKind.Relative), AccessScenario.Json, Ct))!;
        Assert.Equal((2, 2048, 100, 0), (quota.MaxServers, quota.MaxMemoryMb, quota.MaxCpuPercent, quota.Used.Servers));
        Assert.Equal(node, Assert.Single(quota.Nodes).Id);
        Assert.Contains(quota.Games, g => g.TemplateId == css);

        var first = await CreatedAsync(client, new CreateOwnedServerRequest(node, css, "Mine", 1024, 60));
        Assert.Equal((player.Id, 1024, 60), (first.Server.OwnerId!.Value, first.Limits.MemoryMb!.Value, first.Limits.CpuPercent!.Value));
        await ServerApi.WaitForAsync(client, first.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");

        // Theirs: they run it, but its caps and its suspension are not theirs to change.
        await ServerApi.CommandAsync(client, first.Server.Id, "start");
        await ServerApi.WaitForAsync(client, first.Server.Id, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        await ServerApi.CommandAsync(client, first.Server.Id, "stop");
        await ProblemAsync(await client.PostAsync($"/api/v1/servers/{first.Server.Id}/suspend", new SuspendServerRequest(null)), HttpStatusCode.Forbidden,
            null);

        // Over the CPU, then the memory, then the number of servers.
        await ProblemAsync(await client.PostAsync("/api/v1/account/servers", new CreateOwnedServerRequest(node, css, "Too fast", 512, 41)),
            HttpStatusCode.Conflict, AccessErrors.QuotaCpu.Code);
        await ProblemAsync(await client.PostAsync("/api/v1/account/servers", new CreateOwnedServerRequest(node, css, "Too big", 1025, 40)),
            HttpStatusCode.Conflict, AccessErrors.QuotaMemory.Code);
        var second = await CreatedAsync(client, new CreateOwnedServerRequest(node, css, "Second", 1024, 40));
        await ProblemAsync(await client.PostAsync("/api/v1/account/servers", new CreateOwnedServerRequest(node, css, "Third", 128, 1)),
            HttpStatusCode.Conflict, AccessErrors.QuotaServers.Code);

        // Only on the quota's nodes.
        var other = await NodeApi.CreateAsync(admin, "quota-elsewhere");
        await ProblemAsync(await client.PostAsync("/api/v1/account/servers", new CreateOwnedServerRequest(other.Node.Id, css, "Elsewhere", 128, 1)),
            HttpStatusCode.Forbidden, AccessErrors.QuotaNodeNotAllowed.Code);

        // Deleting one of theirs makes room again.
        await ServerApi.WaitForAsync(client, second.Server.Id, s => s.Server.ActiveOperation is null, "second installed");
        using (var deleted = await client.DeleteAsync($"/api/v1/servers/{second.Server.Id}"))
        {
            await AccessScenario.EnsureSuccessAsync(deleted);
        }

        await InProcessAgent.EventuallyAsync(async () => (await QuotaAsync(client)).Used.Servers == 1, "the deleted server freed its place");
        Assert.Equal((1024, 60), ((await QuotaAsync(client)).Used.MemoryMb, (await QuotaAsync(client)).Used.CpuPercent));

        // Another user does not see them.
        using var stranger = await scenario.SignedInWithAsync("quota-stranger");
        await ProblemAsync(await stranger.GetAsync($"/api/v1/servers/{first.Server.Id}"), HttpStatusCode.Forbidden, null);

        var audit = await AuditAsync();
        Assert.Contains(audit, a => a.Action == AuditActions.User.QuotaSet);
        Assert.Contains(audit, a => a.Action == AuditActions.Server.Created && a.Metadata!.Contains(player.Id.ToString(), StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task TwoCreationsAtOnceCannotBothTakeTheLastPlace()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "quota-race");
        var node = agent.Config!.NodeId;
        await GameServerTests.AllocateAsync(admin, node, "0.0.0.0", "27200-27239");
        var player = await scenario.CreateUserAsync("quota-racer");
        using var client = await scenario.SignInAsync("quota-racer");
        var css = (await GameServerTests.TemplateAsync(admin, "css")).Template.Id;
        using (var set = await admin.PutAsync($"/api/v1/users/{player.Id}/quota", new SetUserQuotaRequest(1, 4096, 100, [node])))
        {
            await AccessScenario.EnsureSuccessAsync(set);
        }

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            client.PostAsync("/api/v1/account/servers", new CreateOwnedServerRequest(node, css, $"Race {i}", 512, 10))));
        try
        {
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
            Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.Created), a => Assert.Equal(HttpStatusCode.Conflict, a.StatusCode));
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Servers.CountAsync(s => s.OwnerId == new UserId(player.Id), Ct));
    }

    [DatabaseFact]
    public async Task AQuotaOnANodeIsGivenOnlyByWhoeverMayCreateServersThere()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var node = (await NodeApi.CreateAsync(admin, "quota-escalation")).Node.Id;
        var player = await scenario.CreateUserAsync("quota-escalated");
        using var manager = await scenario.SignedInWithAsync("quota-user-manager", Permissions.User.View, Permissions.User.Manage);

        await ProblemAsync(await manager.PutAsync($"/api/v1/users/{player.Id}/quota", new SetUserQuotaRequest(5, 4096, 100, [node])),
            HttpStatusCode.Forbidden, "access.escalation");
        using (var noNodes = await manager.PutAsync($"/api/v1/users/{player.Id}/quota", new SetUserQuotaRequest(5, 4096, 100, [])))
        {
            await AccessScenario.EnsureSuccessAsync(noNodes);
        }

        await ProblemAsync(await admin.PutAsync($"/api/v1/users/{player.Id}/quota", new SetUserQuotaRequest(1001, 0, 0, [])), HttpStatusCode.BadRequest, null);
        using (var removed = await manager.DeleteAsync($"/api/v1/users/{player.Id}/quota"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        await ProblemAsync(await admin.GetAsync($"/api/v1/users/{player.Id}/quota"), HttpStatusCode.NotFound, AccessErrors.QuotaNotFound.Code);
    }

    private static async Task<ServerDetails> CreatedAsync(PanelClient client, CreateOwnedServerRequest request)
    {
        using var response = await client.PostAsync("/api/v1/account/servers", request);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
    }

    private static async Task<QuotaView> QuotaAsync(PanelClient client) =>
        (await client.Http.GetFromJsonAsync<QuotaView>(new Uri("/api/v1/account/quota", UriKind.Relative), AccessScenario.Json, Ct))!;

    private async Task<List<AuditEntry>> AuditAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking().ToListAsync(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            if (code is not null)
            {
                using var problem = JsonDocument.Parse(body);
                Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
            }
        }
    }
}
