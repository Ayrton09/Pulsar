using System.Collections.Frozen;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>
/// Where a role assignment applies: everywhere, on one node (and every server on it), or on one server.
/// A server target carries its node because node assignments also apply to the node's servers.
/// </summary>
public abstract record AccessTarget
{
    private AccessTarget()
    {
    }

    public abstract PermissionScope Scope { get; }

    public static AccessTarget Global { get; } = new GlobalTarget();

    public static AccessTarget ForNode(NodeId node) => new NodeTarget(node);

    public static AccessTarget ForServer(ServerId server, NodeId node) => new ServerTarget(server, node);

    public sealed record GlobalTarget : AccessTarget
    {
        public override PermissionScope Scope => PermissionScope.Global;
    }

    public sealed record NodeTarget(NodeId Node) : AccessTarget
    {
        public override PermissionScope Scope => PermissionScope.Node;
    }

    public sealed record ServerTarget(ServerId Server, NodeId Node) : AccessTarget
    {
        public override PermissionScope Scope => PermissionScope.Server;
    }
}


/// <summary>
/// What a user may do, computed from their assignments:
/// permissions on server S of node N = global roles ∪ roles on N ∪ roles on S. There are no deny rules in v1.
/// Immutable once built, so one instance can be cached and shared.
/// </summary>
public sealed class EffectivePermissions
{
    private readonly FrozenSet<string> _global;
    private readonly FrozenDictionary<NodeId, FrozenSet<string>> _nodes;
    private readonly FrozenDictionary<ServerId, FrozenSet<string>> _servers;

    private EffectivePermissions(
        FrozenSet<string> global, FrozenDictionary<NodeId, FrozenSet<string>> nodes, FrozenDictionary<ServerId, FrozenSet<string>> servers)
    {
        _global = global;
        _nodes = nodes;
        _servers = servers;
    }

    public static EffectivePermissions None { get; } = new Builder().Build();

    public IReadOnlySet<string> Global => _global;

    public bool IsEmpty => _global.Count == 0 && _nodes.Count == 0 && _servers.Count == 0;

    /// <summary>True when the permission applies at <paramref name="target"/>, directly or inherited from a wider scope.</summary>
    public bool Has(string permission, AccessTarget target) => target switch
    {
        AccessTarget.GlobalTarget => _global.Contains(permission),
        AccessTarget.NodeTarget n => _global.Contains(permission) || Contains(_nodes, n.Node, permission),
        AccessTarget.ServerTarget s => _global.Contains(permission)
            || Contains(_nodes, s.Node, permission)
            || Contains(_servers, s.Server, permission),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    public bool HasGlobal(string permission) => _global.Contains(permission);

    private static bool Contains<TKey>(FrozenDictionary<TKey, FrozenSet<string>> map, TKey key, string permission)
        where TKey : notnull => map.TryGetValue(key, out var set) && set.Contains(permission);

    /// <summary>Collects role assignments and produces an immutable <see cref="EffectivePermissions"/>.</summary>
    public sealed class Builder
    {
        private readonly HashSet<string> _global = new(StringComparer.Ordinal);
        private readonly Dictionary<NodeId, HashSet<string>> _nodes = [];
        private readonly Dictionary<ServerId, HashSet<string>> _servers = [];

        /// <summary>Adds a role granted at <paramref name="target"/>. The role must be of the target's scope.</summary>
        public Builder Add(Role role, AccessTarget target)
        {
            ArgumentNullException.ThrowIfNull(role);
            ArgumentNullException.ThrowIfNull(target);
            if (role.Scope != target.Scope)
            {
                throw new ArgumentException($"A {role.Scope} role cannot be granted at {target.Scope} level.", nameof(target));
            }

            var set = target switch
            {
                AccessTarget.GlobalTarget => _global,
                AccessTarget.NodeTarget n => GetOrAdd(_nodes, n.Node),
                AccessTarget.ServerTarget s => GetOrAdd(_servers, s.Server),
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
            set.UnionWith(role.Permissions);
            return this;
        }

        public EffectivePermissions Build() => new(
            _global.ToFrozenSet(StringComparer.Ordinal),
            _nodes.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet(StringComparer.Ordinal)),
            _servers.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet(StringComparer.Ordinal)));

        private static HashSet<string> GetOrAdd<TKey>(Dictionary<TKey, HashSet<string>> map, TKey key)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                map.Add(key, set);
            }

            return set;
        }
    }
}
