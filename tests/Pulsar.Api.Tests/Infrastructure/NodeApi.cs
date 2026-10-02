using System.Net.Http.Json;
using Pulsar.Api.Nodes;
using Pulsar.Application.Nodes;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>The node endpoints of /api/v1, as the tests use them.</summary>
public static class NodeApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<CreatedNode> CreateAsync(PanelClient admin, string name)
    {
        using var response = await admin.PostAsync("/api/v1/nodes", new CreateNodeRequest(name, null, null));
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<CreatedNode>(AccessScenario.Json, Ct))!;
    }

    public static async Task<NodeDetails> GetAsync(PanelClient admin, Guid id) =>
        (await admin.Http.GetFromJsonAsync<NodeDetails>(new Uri($"/api/v1/nodes/{id}", UriKind.Relative), AccessScenario.Json, Ct))!;

    public static async Task<PingResult> PingAsync(PanelClient admin, Guid id)
    {
        using var ping = await admin.PostAsync($"/api/v1/nodes/{id}/ping");
        await AccessScenario.EnsureSuccessAsync(ping);
        return (await ping.Content.ReadFromJsonAsync<PingResult>(AccessScenario.Json, Ct))!;
    }
}
