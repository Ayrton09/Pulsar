using Pulsar.Application.Abstractions;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Access;

/// <summary>
/// What the signed-in user may see in lists (docs/architecture/02, "Listings filter by visible resources"):
/// everything with a global role, otherwise the nodes and servers their node and server roles apply to. Background
/// work, with no user, sees everything.
/// </summary>
public sealed class VisibleResources(IRequestContext request, IPermissionEvaluator evaluator)
{
    /// <summary>Whether the user sees a server (of <paramref name="node"/>); null when they see every server.</summary>
    public async Task<Func<ServerId, NodeId, bool>?> ServersAsync(CancellationToken cancellationToken)
    {
        if (request.UserId is not { } user)
        {
            return null;
        }

        var permissions = await evaluator.GetAsync(user, cancellationToken);
        return ResourceVisibility.Server.Any(permissions.HasGlobal)
            ? null
            : (server, node) => ResourceVisibility.Server.Any(p => permissions.Has(p, AccessTarget.ForServer(server, node)));
    }

    /// <summary>Whether the user sees a node; null when they see every node.</summary>
    public async Task<Func<NodeId, bool>?> NodesAsync(CancellationToken cancellationToken)
    {
        if (request.UserId is not { } user)
        {
            return null;
        }

        var permissions = await evaluator.GetAsync(user, cancellationToken);
        return ResourceVisibility.Node.Any(permissions.HasGlobal)
            ? null
            : node => ResourceVisibility.Node.Any(p => permissions.Has(p, AccessTarget.ForNode(node)));
    }
}
