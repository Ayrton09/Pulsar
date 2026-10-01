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
}
