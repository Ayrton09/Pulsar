using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Realtime;

/// <summary>
/// The open browser connections, by user, so what ends sessions or changes access can close them: a WebSocket outlives
/// the cookie checks of ordinary requests (docs/architecture/08). Closed connections reconnect on their own, and the
/// reconnection is checked like a new one. A file transfer outlives them the same way: the user's transfers end too.
/// </summary>
public sealed class BrowserConnections(FileTransfers transfers)
{
    private readonly ConcurrentDictionary<string, (UserId User, HubCallerContext Context)> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _scoped = new(StringComparer.Ordinal);

    /// <summary>
    /// Some connection watches lists through node or server roles: notices must then also go to the groups of the
    /// nodes and servers they are about (<see cref="PanelNotifier"/>).
    /// </summary>
    internal bool AnyScopedWatcher => !_scoped.IsEmpty;

    internal void Add(UserId user, HubCallerContext context) => _connections[context.ConnectionId] = (user, context);

    internal void Remove(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
        _scoped.TryRemove(connectionId, out _);
    }

    internal void WatchesScoped(string connectionId) => _scoped[connectionId] = 0;

    /// <summary>The user signed out, was disabled or deleted, changed their password or two-factor setup, or got other roles.</summary>
    public void EndUser(UserId user)
    {
        foreach (var (_, connection) in _connections.Where(c => c.Value.User == user))
        {
            connection.Context.Abort();
        }

        transfers.EndUser(user);
    }

    /// <summary>A role changed: any user's permissions may have changed.</summary>
    public void EndAll()
    {
        foreach (var (_, connection) in _connections)
        {
            connection.Context.Abort();
        }

        transfers.EndAll();
    }
}
