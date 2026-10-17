using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.SignalR.Client;

namespace Pulsar.Agent.Core.Panel;

/// <summary>
/// The current hub connection to the panel, for code that reports to it (servers, operations) without owning the
/// connection. Messages are dropped while disconnected: server states travel again in the next Hello (and their latest
/// change right after it), and operation results are kept in the outbox until delivered.
/// </summary>
public sealed class PanelChannel
{
    private readonly Lock _lock = new();
    private HubConnection? _connection;
    private PanelHttp? _http;
    private Func<CancellationToken, Task>? _connected;
    private Action? _disconnected;

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connection is { State: HubConnectionState.Connected };
            }
        }
    }

    /// <summary>Runs after every Hello the panel accepted (to deliver what waited for a connection).</summary>
    public void OnConnected(Func<CancellationToken, Task> handler)
    {
        lock (_lock)
        {
            _connected += handler;
        }
    }

    /// <summary>Runs when the current connection goes away (not when a new session takes over from it).</summary>
    public void OnDisconnected(Action handler)
    {
        lock (_lock)
        {
            _disconnected += handler;
        }
    }

    /// <summary>Sends without waiting for the panel to process it. False when there is no connection or it failed.</summary>
    public async Task<bool> SendAsync(string method, object argument, CancellationToken cancellationToken)
    {
        var connection = Current();
        if (connection is null)
        {
            return false;
        }

        try
        {
            await connection.SendAsync(method, argument, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Calls the panel and waits until it handled the call: true means it was received.</summary>
    public async Task<bool> DeliverAsync(string method, object argument, CancellationToken cancellationToken)
    {
        var connection = Current();
        if (connection is null)
        {
            return false;
        }

        try
        {
            await connection.InvokeAsync(method, argument, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// An HTTP client authenticated with the current session, for calls that belong to it (file transfers); null
    /// while disconnected. Without a timeout of its own: the caller bounds its requests. Answers it reads whole are
    /// bounded as the protocol's are; a file comes as a stream. The caller disposes it.
    /// </summary>
    public HttpClient? CreateHttpClient()
    {
        PanelHttp? http;
        lock (_lock)
        {
            http = _connection is { State: HubConnectionState.Connected } ? _http : null;
        }

        if (http is null)
        {
            return null;
        }

        var client = new HttpClient(http.Transport.CreateHttpHandler(http.Certificate, http.PanelCertificate), disposeHandler: true)
        {
            BaseAddress = http.Panel,
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = PanelTransport.MaxResponseBytes,
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", http.AccessToken);
        return client;
    }

    internal async Task AttachAsync(HubConnection connection, PanelHttp http, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task>? handlers;
        lock (_lock)
        {
            _connection = connection;
            _http = http;
            handlers = _connected;
        }

        foreach (var handler in handlers?.GetInvocationList().Cast<Func<CancellationToken, Task>>() ?? [])
        {
            await handler(cancellationToken);
        }
    }

    internal void Detach(HubConnection connection)
    {
        Action? handlers = null;
        lock (_lock)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
                _http = null;
                handlers = _disconnected;
            }
        }

        handlers?.Invoke();
    }

    private HubConnection? Current()
    {
        lock (_lock)
        {
            return _connection is { State: HubConnectionState.Connected } connection ? connection : null;
        }
    }
}

/// <summary>How to reach the panel over HTTP within a session.</summary>
internal sealed record PanelHttp(PanelTransport Transport, Uri Panel, string AccessToken, X509Certificate2? Certificate, string? PanelCertificate)
{
    // Holds the session token: never print it.
    public override string ToString() => $"PanelHttp {{ Panel = {Panel} }}";
}
