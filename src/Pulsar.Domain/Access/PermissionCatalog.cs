using System.Collections.Frozen;

namespace Pulsar.Domain.Access;

/// <summary>A permission and the role scopes allowed to contain it.</summary>
public sealed record PermissionDefinition(string Key, IReadOnlySet<PermissionScope> GrantableScopes);

/// <summary>
/// The closed list of permissions. The catalog lives in code because code checks it; the database only stores which
/// roles include which keys (docs/architecture/02).
/// </summary>
public static class PermissionCatalog
{
    private static readonly FrozenSet<PermissionScope> s_serverNodeGlobal =
        FrozenSet.Create(PermissionScope.Server, PermissionScope.Node, PermissionScope.Global);

    private static readonly FrozenSet<PermissionScope> s_nodeGlobal = FrozenSet.Create(PermissionScope.Node, PermissionScope.Global);

    private static readonly FrozenSet<PermissionScope> s_global = FrozenSet.Create(PermissionScope.Global);

    private static readonly FrozenDictionary<string, PermissionDefinition> s_definitions = new PermissionDefinition[]
    {
        new(Permissions.Server.View, s_serverNodeGlobal),
        new(Permissions.Server.Start, s_serverNodeGlobal),
        new(Permissions.Server.Stop, s_serverNodeGlobal),
        new(Permissions.Server.Restart, s_serverNodeGlobal),
        new(Permissions.Server.Kill, s_serverNodeGlobal),
        new(Permissions.Server.ConsoleView, s_serverNodeGlobal),
        new(Permissions.Server.ConsoleWrite, s_serverNodeGlobal),
        new(Permissions.Server.FilesView, s_serverNodeGlobal),
        new(Permissions.Server.FilesEdit, s_serverNodeGlobal),
        new(Permissions.Server.BackupsView, s_serverNodeGlobal),
        new(Permissions.Server.BackupsCreate, s_serverNodeGlobal),
        new(Permissions.Server.BackupsRestore, s_serverNodeGlobal),
        new(Permissions.Server.BackupsDelete, s_serverNodeGlobal),
        new(Permissions.Server.DatabasesView, s_serverNodeGlobal),
        new(Permissions.Server.DatabasesManage, s_serverNodeGlobal),
        new(Permissions.Server.SchedulesView, s_serverNodeGlobal),
        new(Permissions.Server.SchedulesEdit, s_serverNodeGlobal),
        new(Permissions.Server.StartupEdit, s_serverNodeGlobal),
        new(Permissions.Server.SettingsEdit, s_serverNodeGlobal),
        new(Permissions.Server.Install, s_serverNodeGlobal),
        new(Permissions.Server.LimitsEdit, s_nodeGlobal),
        new(Permissions.Server.Create, s_nodeGlobal),
        new(Permissions.Server.Delete, s_nodeGlobal),
        new(Permissions.Node.View, s_nodeGlobal),
        new(Permissions.Node.Manage, s_nodeGlobal),
        new(Permissions.Node.CredentialsManage, s_nodeGlobal),
        new(Permissions.Template.View, s_global),
        new(Permissions.Template.Manage, s_global),
        new(Permissions.User.View, s_global),
        new(Permissions.User.Manage, s_global),
        new(Permissions.Role.Manage, s_global),
        new(Permissions.Audit.View, s_global),
        new(Permissions.Settings.Manage, s_global),
    }.ToFrozenDictionary(d => d.Key, StringComparer.Ordinal);

    public static IEnumerable<PermissionDefinition> All => s_definitions.Values;

    public static FrozenSet<string> AllKeys { get; } = s_definitions.Keys.ToFrozenSet(StringComparer.Ordinal);

    public static bool Exists(string key) => s_definitions.ContainsKey(key);

    /// <summary>True when a role of <paramref name="scope"/> may contain <paramref name="key"/>.</summary>
    public static bool IsGrantableIn(string key, PermissionScope scope) =>
        s_definitions.TryGetValue(key, out var definition) && definition.GrantableScopes.Contains(scope);
}
