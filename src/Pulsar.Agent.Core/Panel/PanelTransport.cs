using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
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
    /// wherever it says (an http:// address included). Checks the panel's certificate with <see cref="PanelChainPolicy"/>.
    /// </summary>
    public virtual HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = PanelChainPolicy() },
    };

    /// <summary>
    /// How the panel's certificate chain is built: from the machine's trusted roots and the certificates the handshake
    /// brought, nothing fetched (docs/architecture/03, "Transport"). Whoever answers at the panel's address chooses the
    /// URLs a certificate names (its issuer's, revocation lists, OCSP), and the agent, SYSTEM, would connect wherever
    /// they say before refusing it; so the panel sends its intermediates. No revocation check, as without a policy.
    /// </summary>
    public static X509ChainPolicy PanelChainPolicy() => new()
    {
        DisableCertificateDownloads = true,
        RevocationMode = X509RevocationMode.NoCheck,
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
    /// Accepts only the panel certificate whose SHA-256 is <paramref name="certificateSha256"/> (<see cref="NodeCode.CertificateHash"/>):
    /// the one a node code pinned at enrollment (Phase 18, docs/architecture/03 "Transport"), whose chain need not lead
    /// to a root the machine trusts. Any other certificate is refused, even one the machine trusts. The machine's
    /// certificate stores are never changed for it. Tests override it for handlers without TLS.
    /// </summary>
    public virtual HttpMessageHandler WithPinnedCertificate(HttpMessageHandler handler, string certificateSha256)
    {
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) => IsPinnedCertificate(certificate, chain, errors, certificateSha256);
        }

        return handler;
    }

    /// <summary>
    /// The certificate is the pinned one, for the host the agent asked for, and valid in time: of the checks it failed
    /// (<paramref name="errors"/>), only the chain's missing trusted root is forgiven (<see cref="X509ChainStatusFlags.UntrustedRoot"/>,
    /// <see cref="X509ChainStatusFlags.PartialChain"/>), and only because the pin names it exactly. Expiry, a start date
    /// still ahead, revocation or any other chain error refuse it, so a pinned certificate that has expired needs pinning again.
    /// </summary>
    internal static bool IsPinnedCertificate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, string certificateSha256)
    {
        if (certificate is null
            || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None
            || !string.Equals(NodeCode.CertificateHash(certificate.GetRawCertData()), certificateSha256, StringComparison.Ordinal))
        {
            return false;
        }

        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None)
        {
            return true;
        }

        // The chain's statuses say which errors: with no chain to read them from, none is forgiven.
        const X509ChainStatusFlags Forgiven = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;
        return chain is not null && chain.ChainStatus.All(status => (status.Status & ~Forgiven) == X509ChainStatusFlags.NoError);
    }

    /// <summary>
    /// Hook for the hub connection options (transport, authentication header, URL, client certificate and the
    /// WebSocket, <see cref="ConnectHubAsync"/>, are already set).
    /// </summary>
    public virtual void ConfigureHub(HttpConnectionOptions options)
    {
    }

    /// <summary>
    /// Opens the hub's WebSocket through <see cref="CreateHttpHandler()"/>, so it checks the panel's certificate as
    /// every other request to the panel does (<paramref name="panelCertificate"/>: the pinned one, if any) and presents
    /// <paramref name="certificate"/> the same way (the WebSocket's own options cannot set how the chain is built).
    /// </summary>
    public async ValueTask<WebSocket> ConnectHubAsync(Uri hub, string accessToken, X509Certificate2? certificate, string? panelCertificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        // Only for the upgrade: the connection it hands over stays with the WebSocket.
        using var invoker = new HttpMessageInvoker(CreateHttpHandler(certificate, panelCertificate), disposeHandler: true);
        try
        {
            await socket.ConnectAsync(hub, invoker, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <param name="certificate">The client certificate to present, if any.</param>
    /// <param name="panelCertificate">The panel certificate the configuration pins (<see cref="AgentConfig.PanelCertificate"/>), if any.</param>
    internal HttpMessageHandler CreateHttpHandler(X509Certificate2? certificate, string? panelCertificate)
    {
        var handler = CreateHttpHandler();
        if (panelCertificate is not null)
        {
            handler = WithPinnedCertificate(handler, panelCertificate);
        }

        return certificate is null ? handler : WithClientCertificate(handler, certificate);
    }

    internal HttpClient CreateClient(Uri panel, X509Certificate2? certificate, string? panelCertificate) =>
        new(CreateHttpHandler(certificate, panelCertificate), disposeHandler: true)
        {
            BaseAddress = panel,
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

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
