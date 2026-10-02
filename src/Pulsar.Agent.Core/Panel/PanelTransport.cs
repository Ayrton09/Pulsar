using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections.Client;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// How the agent reaches the panel: HTTP for enrollment and authentication, WebSockets for the hub. Tests override it
/// to talk to an in-memory panel.
/// </summary>
public class PanelTransport
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public virtual HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Hook for the hub connection options (transport, authentication header and URL are already set).</summary>
    public virtual void ConfigureHub(HttpConnectionOptions options)
    {
    }

    internal HttpClient CreateClient(Uri panel) =>
        new(CreateHttpHandler(), disposeHandler: true) { BaseAddress = panel, Timeout = RequestTimeout };

    /// <summary>Posts a protocol request; a non-success answer becomes <see cref="AgentException"/> (or rejected on 401).</summary>
    internal static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient http, string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path.TrimStart('/'), request, ProtocolJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, path, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(ProtocolJson.Options, cancellationToken)
            ?? throw new AgentException($"The panel sent an empty answer to {path}.");
    }

    internal static async Task<TResponse> GetAsync<TResponse>(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(path.TrimStart('/'), UriKind.Relative), cancellationToken);
        await EnsureSuccessAsync(response, path, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(ProtocolJson.Options, cancellationToken)
            ?? throw new AgentException($"The panel sent an empty answer to {path}.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new AgentRejectedException("The panel rejected this node's credential (revoked, disabled or deleted node).");
        }

        AgentError? error = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                error = await response.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                // Not an agent error body (e.g. a proxy page); report the status alone.
            }
        }

        throw new AgentException(error is null
            ? $"The panel answered {(int)response.StatusCode} {response.ReasonPhrase} to {path}."
            : $"{error.Message} ({error.Code})");
    }
}
