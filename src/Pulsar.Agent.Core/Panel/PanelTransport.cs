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

    /// <summary>
    /// The most of an answer the agent reads into memory (64 KiB): the protocol's answers are a few KiB at most, and
    /// whatever answers at the address could otherwise make it buffer up to 2 GiB. Transfers stream their files.
    /// </summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// Never follows a redirect: a 307 or 308 would send an enrollment, a challenge's answer or a new key's proof on to
    /// wherever it says (an http:// address included).
    /// </summary>
    public virtual HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
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
        new(CreateHttpHandler(certificate), disposeHandler: true) { BaseAddress = panel, Timeout = RequestTimeout, MaxResponseContentBufferSize = MaxResponseBytes };

    /// <summary>
    /// Posts a protocol request; a non-success answer becomes <see cref="AgentException"/> (rejected on the panel's 401),
    /// and one that is not the protocol's (another service's page or refusal), <see cref="PanelNotAnsweringException"/>.
    /// </summary>
    internal static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient http, string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path.TrimStart('/'), request, ProtocolJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, path, cancellationToken);
        return await ReadAsync<TResponse>(response, path, cancellationToken);
    }

    internal static async Task<TResponse> GetAsync<TResponse>(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(path.TrimStart('/'), UriKind.Relative), cancellationToken);
        await EnsureSuccessAsync(response, path, cancellationToken);
        return await ReadAsync<TResponse>(response, path, cancellationToken);
    }

    private static async Task<TResponse> ReadAsync<TResponse>(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(ProtocolJson.Options, cancellationToken)
                ?? throw new PanelNotAnsweringException($"The address sent an empty answer to {path}: no panel answers the agent protocol there now.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            // A page of whatever else answers there (a port given to another service later).
            throw new PanelNotAnsweringException($"The address answered {path} with something that is not the agent protocol: no panel answers it there now.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || (int)response.StatusCode >= 500 || (int)response.StatusCode is >= 300 and < 400)
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
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden && error is null)
        {
            // The panel says why in the protocol's words; a refusal without them is another service's (docs/architecture/03, "Fallback").
            throw new PanelNotAnsweringException($"The address answered {(int)response.StatusCode} {response.ReasonPhrase} to {path} without the panel's error: no panel answers the agent protocol there now.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new AgentRejectedException("The panel rejected this node's credential (revoked, disabled or deleted node).");
        }

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
            var error = await response.Content.ReadFromJsonAsync<AgentError>(ProtocolJson.Options, cancellationToken);
            return string.IsNullOrEmpty(error?.Code) ? null : error;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            // Not an agent error body (e.g. a proxy page); report the status alone.
            return null;
        }
    }
}
