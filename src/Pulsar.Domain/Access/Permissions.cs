namespace Pulsar.Domain.Access;

/// <summary>
/// Permission keys (docs/architecture/02). They are stored in the database and referenced by the frontend,
/// so a key never changes once released; <see cref="PermissionCatalog"/> says in which scopes each one can be granted.
/// </summary>
public static class Permissions
{
    public static class Server
    {
        public const string View = "server.view";
        public const string Start = "server.start";
        public const string Stop = "server.stop";
        public const string Restart = "server.restart";
        public const string Kill = "server.kill";
        public const string ConsoleView = "server.console.view";
        public const string ConsoleWrite = "server.console.write";
        public const string FilesView = "server.files.view";
        public const string FilesEdit = "server.files.edit";
        public const string BackupsView = "server.backups.view";
        public const string BackupsCreate = "server.backups.create";
        public const string BackupsRestore = "server.backups.restore";
        public const string BackupsDelete = "server.backups.delete";
        public const string SchedulesView = "server.schedules.view";
        public const string SchedulesEdit = "server.schedules.edit";
        public const string StartupEdit = "server.startup.edit";
        public const string SettingsEdit = "server.settings.edit";
        public const string Install = "server.install";

        /// <summary>CPU and memory caps: grantable on a node or globally only, so a server's owner cannot raise their own.</summary>
        public const string LimitsEdit = "server.limits.edit";
        public const string Create = "server.create";
        public const string Delete = "server.delete";
    }

    public static class Node
    {
        public const string View = "node.view";
        public const string Manage = "node.manage";
        public const string CredentialsManage = "node.credentials.manage";
    }

    public static class Template
    {
        public const string View = "template.view";
        public const string Manage = "template.manage";
    }

    public static class User
    {
        public const string View = "user.view";
        public const string Manage = "user.manage";
    }

    public static class Role
    {
        public const string Manage = "role.manage";
    }

    public static class Audit
    {
        public const string View = "audit.view";
    }

    public static class Settings
    {
        public const string Manage = "settings.manage";
    }
}
