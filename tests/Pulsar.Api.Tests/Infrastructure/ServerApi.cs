using System.Net;
using System.Net.Http.Json;
using Pulsar.Api.Servers;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>The server endpoints of /api/v1, as the tests use them.</summary>
public static class ServerApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<TemplateView> TestTemplateAsync(PanelClient client)
    {
        var templates = await client.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct);
        return templates!.Single(t => t.IsTestGame);
    }

    public static async Task<ServerDetails> CreateAsync(PanelClient client, Guid nodeId, string name)
    {
        var template = await TestTemplateAsync(client);
        using var response = await client.PostAsync("/api/v1/servers", new CreateServerRequest(nodeId, template.Id, name, null));
        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
    }

    public static async Task<ServerDetails?> GetAsync(PanelClient client, Guid id)
    {
        using var response = await client.Http.GetAsync(new Uri($"/api/v1/servers/{id}", UriKind.Relative), Ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await AccessScenario.EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct);
    }

    public static async Task<AgentCommandResult> CommandAsync(PanelClient client, Guid id, string command)
    {
        using var response = await client.PostAsync($"/api/v1/servers/{id}/{command}");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<AgentCommandResult>(AccessScenario.Json, Ct))!;
    }

    /// <summary>Polls the server until <paramref name="condition"/> holds.</summary>
    public static async Task<ServerDetails> WaitForAsync(PanelClient client, Guid id, Func<ServerDetails, bool> condition, string what)
    {
        ServerDetails? last = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            last = await GetAsync(client, id);
            return last is not null && condition(last);
        }, () => $"{what} (last: {last?.Server.InstallState}/{last?.Server.RuntimeState}/{last?.Server.LastStateReason}/{last?.Server.ActiveOperation?.Type})");
        return last!;
    }
}
