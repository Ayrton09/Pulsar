using Microsoft.AspNetCore.Identity;
using Pulsar.Domain.Common;

namespace Pulsar.Infrastructure.Identity;

/// <summary>
/// A panel user (table <c>users</c>). Lives in Infrastructure because it is an ASP.NET Core Identity type; the rest of
/// the system only knows <see cref="UserId"/> (ADR-0009).
/// </summary>
public sealed class PulsarUser : IdentityUser<UserId>
{
    public const int MaxUserNameLength = 64;
    public const int MaxDisplayNameLength = 128;

    public string? DisplayName { get; set; }

    /// <summary>A disabled user cannot sign in; existing sessions end at the next security stamp validation.</summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
