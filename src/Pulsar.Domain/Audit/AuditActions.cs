namespace Pulsar.Domain.Audit;

/// <summary>Audited action keys (docs/architecture/08). Stored in the database: a key never changes once released.</summary>
public static class AuditActions
{
    public static class Auth
    {
        public const string Login = "auth.login";
        public const string LoginFailed = "auth.login_failed";
        public const string Logout = "auth.logout";
        public const string Setup = "auth.setup";
        public const string PasswordChanged = "auth.password_changed";
    }

    public static class User
    {
        public const string Created = "user.created";
        public const string Updated = "user.updated";
        public const string Deleted = "user.deleted";
        public const string PasswordReset = "user.password_reset";
    }

    public static class Role
    {
        public const string Created = "role.created";
        public const string Updated = "role.updated";
        public const string Deleted = "role.deleted";
        public const string Assigned = "role.assigned";
        public const string Revoked = "role.revoked";
    }
}
