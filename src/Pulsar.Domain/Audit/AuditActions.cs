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
        public const string ProfileUpdated = "auth.profile_updated";
        public const string TwoFactorEnabled = "auth.two_factor_enabled";
        public const string TwoFactorDisabled = "auth.two_factor_disabled";
        public const string TwoFactorFailed = "auth.two_factor_failed";
        public const string RecoveryCodeUsed = "auth.recovery_code_used";
        public const string RecoveryCodesRenewed = "auth.recovery_codes_renewed";
    }

    public static class User
    {
        public const string Created = "user.created";
        public const string Updated = "user.updated";
        public const string Deleted = "user.deleted";
        public const string PasswordReset = "user.password_reset";
        public const string TwoFactorReset = "user.two_factor_reset";
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
        public const string CredentialRotationRequested = "node.credential_rotation_requested";
        public const string CredentialRotationStarted = "node.credential_rotation_started";
        public const string CredentialRotated = "node.credential_rotated";
        public const string AllocationsCreated = "node.allocations_created";
        public const string AllocationDeleted = "node.allocation_deleted";
    }

    public static class Server
    {
        public const string Created = "server.created";
        public const string Deleted = "server.deleted";
        public const string DeleteRequested = "server.delete_requested";
        public const string Started = "server.start";
        public const string Stopped = "server.stop";
        public const string Restarted = "server.restart";
        public const string Killed = "server.kill";
        public const string InstallRequested = "server.install_requested";
        public const string UpdateRequested = "server.update_requested";
        public const string SettingsChanged = "server.settings_changed";
        public const string PortsChanged = "server.ports_changed";
        public const string OperationCancelRequested = "server.operation_cancel_requested";
        public const string ConsoleCommand = "server.console_command";
        public const string FileWritten = "server.file_written";
        public const string FileDownloaded = "server.file_downloaded";
        public const string FileMoved = "server.file_moved";
        public const string FileDeleted = "server.file_deleted";
        public const string DirectoryCreated = "server.directory_created";
        public const string BackupRequested = "server.backup_requested";
        public const string BackupRestoreRequested = "server.backup_restore_requested";
        public const string BackupDeleted = "server.backup_deleted";
        public const string BackupDownloaded = "server.backup_downloaded";
        public const string BackupLocked = "server.backup_locked";
        public const string BackupUnlocked = "server.backup_unlocked";
        public const string BackupLimitChanged = "server.backup_limit_changed";
        public const string ScheduleCreated = "server.schedule_created";
        public const string ScheduleChanged = "server.schedule_changed";
        public const string ScheduleDeleted = "server.schedule_deleted";
        public const string ScheduleRunRequested = "server.schedule_run_requested";
    }

    public static class Template
    {
        public const string Imported = "template.imported";
    }

    public static class PanelKey
    {
        public const string Rotated = "panel_key.rotated";
        public const string Retired = "panel_key.retired";
    }

    public static class Notification
    {
        public const string ChannelCreated = "notification.channel_created";
        public const string ChannelUpdated = "notification.channel_updated";
        public const string ChannelDeleted = "notification.channel_deleted";
        public const string Tested = "notification.tested";
        public const string SettingsUpdated = "notification.settings_updated";
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
