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

    public static class Node
    {
        public const string Created = "node.created";
        public const string Updated = "node.updated";
        public const string Deleted = "node.deleted";
        public const string Disabled = "node.disabled";
        public const string Enabled = "node.enabled";
        public const string EnrollmentTokenCreated = "node.enrollment_token_created";
        public const string Enrolled = "node.enrolled";
        public const string EnrollmentFailed = "node.enrollment_failed";
        public const string CredentialRevoked = "node.credential_revoked";
        public const string AuthenticationFailed = "node.authentication_failed";
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
