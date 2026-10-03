using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Realtime;

/// <summary>
/// The open browser connections, by user, so what ends sessions or changes access can close them: a WebSocket outlives
/// the cookie checks of ordinary requests (docs/architecture/08). Closed connections reconnect on their own, and the
/// reconnection is checked like a new one.
/// </summary>
public sealed class BrowserConnections
{
    private readonly ConcurrentDictionary<string, (UserId User, HubCallerContext Context)> _connections = new(StringComparer.Ordinal);

    internal void Add(UserId user, HubCallerContext context) => _connections[context.ConnectionId] = (user, context);

    internal void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>The user was disabled or deleted, changed their password or two-factor setup, or got other roles.</summary>
    public void EndUser(UserId user)
    {
        foreach (var (_, connection) in _connections.Where(c => c.Value.User == user))
        {
            connection.Context.Abort();
        }
    }

    /// <summary>A role changed: any user's permissions may have changed.</summary>
    public void EndAll()
    {
        foreach (var (_, connection) in _connections)
        {
            connection.Context.Abort();
        }
    }
}
