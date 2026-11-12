using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Security;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Realtime;

/// <summary>
/// The browsers' live channel (docs/architecture/07, "Real time"): one connection per tab, signed in by cookie. A page
/// says what it watches and gets pushed changes, usage and console output instead of polling. Every watch checks the
/// user's permission when it starts; a refused watch answers false or null (an exception would be logged as an error
/// for every user without the permission). A connection opens only for a session that is still valid (checked now,
/// not at the cookie's next minute check), and <see cref="BrowserConnections"/> closes it when access changes.
/// </summary>
[Authorize(AuthenticationSchemes = "Identity.Application")]
internal sealed class PanelHub(
    IPermissionEvaluator permissions,
    IPulsarDbContext db,
    ConsoleMirrors consoles,
    LiveMetrics metrics,
    SignInManager<PulsarUser> signIn,
    BrowserConnections connections) : Hub<IPanelClient>
{
    public const string Path = "/hubs/panel";

    /// <summary>Notices about every server, for users who see every server (a global role).</summary>
    public const string ServersGroup = "servers";

    /// <summary>Notices and usage of every node, for users who see every node's details.</summary>
    public const string NodesGroup = "nodes";

    /// <summary>
    /// Notices about every node, without their usage, for users who may create servers on every node but not see
    /// their details.
    /// </summary>
    public const string NodesListGroup = "nodes-list";

    /// <summary>Usage and operation progress of one server (its page).</summary>
    public static string ServerGroup(ServerId server, NodeId node) => $"server:{server.Value}:node:{node.Value}";

    private readonly record struct ServerWatchKey(ServerId Server);

    /// <summary>Notices about one server, for users who see it through a role on it.</summary>
    public static string ServerListGroup(ServerId server) => $"server-list:{server.Value}";

    /// <summary>Notices about the servers of one node, for users who see them through a role on the node.</summary>
    public static string NodeServersGroup(NodeId node) => $"node-servers:{node.Value}";

    /// <summary>Notices and usage of one node, for users who see its details through a role on it.</summary>
    public static string NodeGroup(NodeId node) => $"node:{node.Value}";

    /// <summary>Notices about one node, without its usage, for users who may only create servers on it through a role on it.</summary>
    public static string NodeListGroup(NodeId node) => $"node-list:{node.Value}";

    /// <summary>
    /// Notices when servers change (lists, the dashboard, server pages): about every server with a global role, else
    /// about the servers the user's node and server roles let them see. False when they see none.
    /// </summary>
    public async Task<bool> WatchServers()
    {
        if (await PermissionsAsync() is not { } granted)
        {
            return false;
        }

        if (ResourceVisibility.Server.Any(granted.HasGlobal))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, ServersGroup);
            return true;
        }

        var groups = ResourceVisibility.Server.SelectMany(granted.NodesWith).Distinct().Select(NodeServersGroup)
            .Concat(ResourceVisibility.Server.SelectMany(granted.ServersWith).Distinct().Select(ServerListGroup))
            .ToList();
        return await JoinScopedAsync(groups);
    }

    /// <summary>
    /// Notices and usage of nodes (lists, node pages, the dashboard): every node with a global role, else the nodes the
    /// user's node roles let them see. Whoever may only create servers on a node (the create dialog lists it, with its
    /// name and state) gets its notices but not its usage. False when they see none.
    /// </summary>
    public async Task<bool> WatchNodes()
    {
        if (await PermissionsAsync() is not { } granted)
        {
            return false;
        }

        if (ResourceVisibility.NodeDetails.Any(granted.HasGlobal))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, NodesGroup);
            return true;
        }

        var detailed = ResourceVisibility.NodeDetails.SelectMany(granted.NodesWith).Distinct().ToList();
        var groups = detailed.Select(NodeGroup).ToList();
        if (granted.HasGlobal(Permissions.Server.Create))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, NodesListGroup);
            await JoinScopedAsync(groups);
            return true;
        }

        groups.AddRange(granted.NodesWith(Permissions.Server.Create).Except(detailed).Select(NodeListGroup));
        return await JoinScopedAsync(groups);
    }

    /// <summary>
    /// Joins the groups of what scoped roles let the user see. They hold for the connection's life: changing a user's
    /// roles closes their connections (<see cref="BrowserConnections"/>), and the reconnection joins again.
    /// </summary>
    private async Task<bool> JoinScopedAsync(List<string> groups)
    {
        if (groups.Count == 0)
        {
            return false;
        }

        connections.WatchesScoped(Context.ConnectionId);
        foreach (var group in groups)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }

        return true;
    }

    /// <summary>
    /// Usage and operation progress of one server, with its latest usage sample (when it runs). Null when the server does
    /// not exist or the user may not see it.
    /// </summary>
    public Task<ServerWatch?> WatchServer(Guid serverId) => WatchServerAsync(serverId, expectedNode: null);

    public Task<ServerWatch?> WatchServerAtNode(Guid serverId, Guid nodeId) => WatchServerAsync(serverId, new NodeId(nodeId));

    private async Task<ServerWatch?> WatchServerAsync(Guid serverId, NodeId? expectedNode)
    {
        if (await FindServerAsync(serverId, Permissions.Server.View) is not { } server
            || (expectedNode is { } expected && server.Node != expected))
        {
            return null;
        }

        var key = new ServerWatchKey(server.Id);
        if (Context.Items.TryGetValue(key, out var previous) && previous is NodeId oldNode && oldNode != server.Node)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ServerGroup(server.Id, oldNode));
        }

        Context.Items[key] = server.Node;
        await Groups.AddToGroupAsync(Context.ConnectionId, ServerGroup(server.Id, server.Node));
        return new ServerWatch(metrics.Find(server.Id, server.Node));
    }

    public Task UnwatchServer(Guid serverId)
    {
        var id = new ServerId(serverId);
        var key = new ServerWatchKey(id);
        if (Context.Items.TryGetValue(key, out var previous) && previous is NodeId node)
        {
            Context.Items.Remove(key);
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, ServerGroup(id, node));
        }

        return Task.CompletedTask;
    }

    public Task UnwatchServerAtNode(Guid serverId, Guid nodeId)
    {
        var id = new ServerId(serverId);
        var node = new NodeId(nodeId);
        var key = new ServerWatchKey(id);
        if (Context.Items.TryGetValue(key, out var previous) && previous is NodeId current && current == node)
        {
            Context.Items.Remove(key);
        }

        // A delayed old-node cleanup must not remove the new node's subscription.
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, ServerGroup(id, node));
    }

    /// <summary>
    /// Starts receiving a server's console output and returns what the panel holds after <paramref name="fromSequence"/>
    /// (the last sequence this browser has; null for everything). Output pushed while this call runs may come before
    /// its answer: the browser keeps it until then and drops what it already has.
    /// </summary>
    public async Task<ConsoleSnapshot?> WatchConsole(Guid serverId, long? fromSequence) =>
        await FindServerAsync(serverId, Permissions.Server.ConsoleView, whileSuspended: false) is { } server
            ? await consoles.WatchAsync(Context.ConnectionId, server.Id, server.Node, fromSequence, Context.ConnectionAborted)
            : null;

    public Task UnwatchConsole(Guid serverId) => consoles.UnwatchAsync(Context.ConnectionId, new ServerId(serverId));

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.GetUserId() is not { } claimed)
        {
            Context.Abort();
            return;
        }

        // Counted first, checked then: whatever ends the user's sessions while the check runs (a disable, a password
        // changed elsewhere) finds this connection and closes it, and what ended them before is seen by the check. A
        // disabled user, or a session older than a password change, is refused even if its cookie has not been checked
        // again yet.
        connections.Add(claimed, Context);
        if (await signIn.ValidateSecurityStampAsync(Context.User) is not { } user || user.Id != claimed)
        {
            connections.Remove(Context.ConnectionId);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        consoles.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private async Task<EffectivePermissions?> PermissionsAsync() =>
        Context.User?.GetUserId() is { } user ? await permissions.GetAsync(user, Context.ConnectionAborted) : null;

    /// <summary>
    /// The server and its node, if it exists and the user holds <paramref name="permission"/> on it; a suspended one
    /// (1.0.24) only for those who may suspend it, unless <paramref name="whileSuspended"/> (its page).
    /// </summary>
    private async Task<(ServerId Id, NodeId Node)?> FindServerAsync(Guid serverId, string permission, bool whileSuspended = true)
    {
        var id = new ServerId(serverId);
        var server = await db.Servers.Where(s => s.Id == id).Select(s => new { s.NodeId, s.SuspendedAt }).SingleOrDefaultAsync(Context.ConnectionAborted);
        if (server is null || Context.User?.GetUserId() is not { } user)
        {
            return null;
        }

        var target = AccessTarget.ForServer(id, server.NodeId);
        var granted = await permissions.GetAsync(user, Context.ConnectionAborted);
        return granted.Has(permission, target) && (whileSuspended || server.SuspendedAt is null || granted.Has(Permissions.Server.Suspend, target))
            ? (id, server.NodeId)
            : null;
    }
}

/// <summary>Refuses browser hub connections opened by another site (cross-site WebSocket hijacking), before any cookie is used.</summary>
internal static class PanelHubOrigin
{
    public static IApplicationBuilder UsePanelHubOriginCheck(this IApplicationBuilder app) =>
        app.UseWhen(context => context.Request.Path.StartsWithSegments(PanelHub.Path), branch => branch.Use(async (context, next) =>
        {
            if (context.Request.Headers.Origin is [{ } origin] && !SameAuthority(origin, context.Request.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        }));

    /// <summary>Host and port only: behind a TLS proxy the panel may see plain HTTP while the page is HTTPS.</summary>
    private static bool SameAuthority(string origin, HostString host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", host.Value, StringComparison.OrdinalIgnoreCase);
}
