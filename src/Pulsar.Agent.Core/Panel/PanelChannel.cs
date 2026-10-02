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
    private Func<CancellationToken, Task>? _connected;

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

    internal async Task AttachAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task>? handlers;
        lock (_lock)
        {
            _connection = connection;
            handlers = _connected;
        }

        foreach (var handler in handlers?.GetInvocationList().Cast<Func<CancellationToken, Task>>() ?? [])
        {
            await handler(cancellationToken);
        }
    }

    internal void Detach(HubConnection connection)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_connection, connection))
            {
                _connection = null;
            }
        }
    }

    private HubConnection? Current()
    {
        lock (_lock)
        {
            return _connection is { State: HubConnectionState.Connected } connection ? connection : null;
        }
    }
}
