using System.ComponentModel.DataAnnotations;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Access;

public sealed record RoleReference(Guid Id, string Name);

/// <summary>A role the user holds on one node (and its servers) or on one server.</summary>
/// <param name="Scope"><see cref="PermissionScope.Node"/> or <see cref="PermissionScope.Server"/>.</param>
/// <param name="TargetId">The node or the server.</param>
public sealed record AccessReference(PermissionScope Scope, Guid TargetId, string TargetName, RoleReference Role);

public sealed record UserResponse(
    Guid Id,
    string UserName,
    string? DisplayName,
    string? Email,
    bool IsDisabled,
    bool TwoFactorEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<RoleReference> Roles,
    IReadOnlyList<AccessReference> Access,
    int ApiKeys = 0,
    int SshKeys = 0,
    IReadOnlyList<SsoKind>? SsoLinks = null,
    string? ExternalId = null);

public sealed record CreateUserRequest(
    [property: Required, StringLength(PulsarUser.MaxUserNameLength, MinimumLength = 1)] string UserName,
    [property: Required, StringLength(256)] string Password,
    [property: StringLength(PulsarUser.MaxDisplayNameLength)] string? DisplayName,
    [property: EmailAddress, StringLength(256)] string? Email,
    IReadOnlyList<Guid>? RoleIds,
    [property: StringLength(ExternalIds.MaxLength)] string? ExternalId = null);

/// <summary>Full replacement of the editable profile fields (null clears display name and email).</summary>
public sealed record UpdateUserRequest(
    [property: StringLength(PulsarUser.MaxDisplayNameLength)] string? DisplayName,
    [property: EmailAddress, StringLength(256)] string? Email,
    bool IsDisabled);

public sealed record ResetPasswordRequest([property: Required, StringLength(256)] string NewPassword);

/// <summary>The complete set of global roles the user should have.</summary>
public sealed record SetUserRolesRequest([property: Required] IReadOnlyList<Guid> RoleIds);

/// <summary>A user's quota (1.0.24): what it allows, the nodes, and what their servers use of it.</summary>
public sealed record UserQuotaResponse(int MaxServers, int MaxMemoryMb, int MaxCpuPercent, IReadOnlyList<Guid> NodeIds, QuotaUsage Used);

/// <summary>Gives a user a quota, or changes it (1.0.24): totals for the servers they own, and where they may create them.</summary>
public sealed record SetUserQuotaRequest(
    [property: Range(0, UserQuota.MaxServersLimit)] int MaxServers,
    [property: Range(0, int.MaxValue)] int MaxMemoryMb,
    [property: Range(0, UserQuota.MaxCpuPercentLimit)] int MaxCpuPercent,
    [property: MaxLength(UserQuota.MaxNodes)] IReadOnlyList<Guid>? NodeIds);
