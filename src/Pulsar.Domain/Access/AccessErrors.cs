using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>Errors of the access module. Codes are part of the API contract (ProblemDetails "code").</summary>
public static class AccessErrors
{
    public static DomainError RoleNameInvalid { get; } =
        new("role.name_invalid", $"The role name must have between 1 and {Role.MaxNameLength} characters.");

    public static DomainError RoleDescriptionTooLong { get; } =
        new("role.description_too_long", $"The role description must have at most {Role.MaxDescriptionLength} characters.");

    public static DomainError SystemRoleImmutable { get; } =
        new("role.system_immutable", "System roles cannot be modified or deleted.");

    public static DomainError UnknownPermission(string key) =>
        new("role.permission_unknown", $"Unknown permission '{key}'.");

    public static DomainError PermissionNotGrantable(string key, PermissionScope scope) =>
        new("role.permission_not_grantable", $"Permission '{key}' cannot be part of a {scope.ToString().ToLowerInvariant()} role.");

    public static DomainError ScopeMismatch(PermissionScope roleScope, PermissionScope targetScope) =>
        new("access.scope_mismatch",
            $"A {roleScope.ToString().ToLowerInvariant()} role cannot be assigned at {targetScope.ToString().ToLowerInvariant()} level.");

    public static DomainError SelfChange { get; } =
        new("access.self_change", "Users cannot change their own role assignments.");

    public static DomainError Escalation(IEnumerable<string> missing) =>
        new("access.escalation", $"You can only grant permissions you hold yourself. Missing: {string.Join(", ", missing)}.");

    public static DomainError LastAdministrator { get; } =
        new("access.last_administrator", "The last global Administrator assignment cannot be removed.");

    public static DomainError RoleNotFound { get; } = new("role.not_found", "The role does not exist.");

    public static DomainError ApiKeyNameInvalid { get; } =
        new("api_key.name_invalid", $"An API key's name is one line of 1 to {ApiKey.MaxNameLength} characters.");

    public static DomainError ApiKeyPermissionsInvalid { get; } =
        new("api_key.permissions_invalid", "List at least one existing permission, or none to give the key all of yours.");

    public static DomainError ApiKeyExpiryInvalid { get; } = new("api_key.expiry_invalid", "An API key must expire in the future.");

    public static DomainError ApiKeyTooMany { get; } =
        new("api_key.too_many", $"A user can have at most {ApiKey.MaxPerUser} API keys; revoke one first.");

    public static DomainError ApiKeyNotFound { get; } = new("api_key.not_found", "The API key does not exist.");

    public static DomainError SshKeyInvalid { get; } =
        new("ssh_key.invalid", "This is not an SSH public key in OpenSSH format: one line with the key's type, the key and an optional comment.");

    public static DomainError SshKeyPrivate { get; } =
        new("ssh_key.private_key", "This is a private key: never give it to anyone. Paste the public key (the .pub file) instead.");

    public static DomainError SshKeyWrongFormat { get; } =
        new("ssh_key.wrong_format",
            "This public key is in the SSH2 (RFC 4716) format: paste it in OpenSSH format, as PuTTYgen shows it in \"Public key for pasting into OpenSSH authorized_keys file\".");

    public static DomainError SshKeyUnsupported { get; } =
        new("ssh_key.unsupported_type",
            "Only Ed25519, ECDSA (P-256, P-384, P-521) and RSA keys are accepted: not DSA, security keys (sk-…) or certificates.");

    public static DomainError SshKeyRsaTooShort { get; } =
        new("ssh_key.rsa_too_short", $"An RSA key must have at least {SshPublicKey.MinRsaBits} bits.");

    public static DomainError SshKeyNameInvalid { get; } =
        new("ssh_key.name_invalid", $"An SSH key's name is one line of 1 to {SshKey.MaxNameLength} characters.");

    public static DomainError SshKeyTooMany { get; } =
        new("ssh_key.too_many", $"A user can have at most {SshKey.MaxPerUser} SSH keys; delete one first.");

    public static DomainError SshKeyDuplicate { get; } = new("ssh_key.duplicate", "You have added this SSH key already.");

    public static DomainError SshKeyNotFound { get; } = new("ssh_key.not_found", "The SSH key does not exist.");

    public static DomainError RoleNameTaken { get; } = new("role.name_taken", "Another role already has this name.");

    public static DomainError RoleInUse { get; } =
        new("role.in_use", "The role is assigned to users; remove those assignments before deleting it.");

    public static DomainError UserNotFound { get; } = new("user.not_found", "The user does not exist.");
}
