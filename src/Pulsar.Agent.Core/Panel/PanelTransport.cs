using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
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

    /// <summary>
    /// Presents <paramref name="certificate"/>, the node key, as the TLS client certificate (Phase 16). Only the panel's
    /// agents' endpoint asks for one; elsewhere it is never sent. Tests override it for handlers without TLS.
    /// </summary>
    public virtual HttpMessageHandler WithClientCertificate(HttpMessageHandler handler, X509Certificate2 certificate)
    {
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.SslOptions.ClientCertificates = [certificate];
            // Whatever issuers the server lists: a self-signed certificate is never among them.
            sockets.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        return handler;
    }

    /// <summary>
    /// Hook for the hub connection options (transport, authentication header, URL and client certificate are already
    /// set).
    /// </summary>
    public virtual void ConfigureHub(HttpConnectionOptions options)
    {
    }

    internal HttpMessageHandler CreateHttpHandler(X509Certificate2? certificate) =>
        certificate is null ? CreateHttpHandler() : WithClientCertificate(CreateHttpHandler(), certificate);

    internal HttpClient CreateClient(Uri panel, X509Certificate2? certificate = null) =>
        new(CreateHttpHandler(certificate), disposeHandler: true) { BaseAddress = panel, Timeout = RequestTimeout };

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

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || (int)response.StatusCode >= 500)
        {
            throw new PanelNotAnsweringException($"The address answered {(int)response.StatusCode} {response.ReasonPhrase} to {path}: no panel answers the agent protocol there now.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                : null;
            throw new PanelBusyException($"The panel answered 429 Too Many Requests to {path}: too many attempts from this address.", retryAfter);
        }

        // Read once: the body cannot be read again.
        var error = await ReadErrorAsync(response, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && error is { Code: CertificateRequiredException.Code })
        {
            throw new CertificateRequiredException(error.Message, error.AgentsAddress);
        }

        throw new AgentException(error is null
            ? $"The panel answered {(int)response.StatusCode} {response.ReasonPhrase} to {path}."
            : $"{error.Message} ({error.Code})");
    }

    private static async Task<AgentError?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            // Not an agent error body (e.g. a proxy page); report the status alone.
            return null;
        }
    }
}
