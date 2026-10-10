using System.Security.Cryptography;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Nodes;

/// <summary>
/// An enrollment token many machines can use (Phase 14, docs/architecture/03, "Bulk enrollment"): each use creates a
/// new node named after the machine, so one command (or one MSI deployment through Intune or a group policy) enrolls a
/// whole fleet. It is limited (a number of uses and an expiry) and revocable, and only its SHA-256 is stored. A token
/// that leaked is the risk, so by default the nodes it creates wait for an administrator's approval before their agents
/// may connect; a use never takes over an existing node.
/// </summary>
public sealed class ReusableEnrollmentToken
{
    public const int MaxNameLength = 64;
    public const int MaxUsesLimit = 1000;
    public const int MaxLifetimeDays = 90;

    /// <summary>The value starts with it, so an agent and the panel can tell it from a single-use token at a glance.</summary>
    public const string Prefix = "pulsar_enroll_";

    private ReusableEnrollmentToken()
    {
        // For EF Core materialization.
        Name = null!;
        TokenHash = null!;
        TokenPrefix = null!;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public byte[] TokenHash { get; private set; }

    /// <summary>The first characters of the value, to recognize it in a deployment script.</summary>
    public string TokenPrefix { get; private set; }

    public int MaxUses { get; private set; }

    public int Uses { get; private set; }

    /// <summary>Whether the nodes it creates wait for an administrator's approval before their agents may connect.</summary>
    public bool RequireApproval { get; private set; }

    public UserId? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Creates a token; the plain value is returned once and never stored.</summary>
    public static Result<(ReusableEnrollmentToken Token, string PlainValue)> Issue(string name, int maxUses, TimeSpan lifetime, bool requireApproval,
        UserId? createdBy, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            return EnrollmentTokenErrors.NameInvalid;
        }

        if (maxUses is < 1 or > MaxUsesLimit)
        {
            return EnrollmentTokenErrors.UsesInvalid;
        }

        if (lifetime < TimeSpan.FromHours(1) || lifetime > TimeSpan.FromDays(MaxLifetimeDays))
        {
            return EnrollmentTokenErrors.LifetimeInvalid;
        }

        var plain = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new ReusableEnrollmentToken
        {
            Id = Guid.CreateVersion7(now),
            Name = trimmed,
            TokenHash = Hash(plain),
            TokenPrefix = plain[..(Prefix.Length + 6)],
            MaxUses = maxUses,
            RequireApproval = requireApproval,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
        return (token, plain);
    }

    public static byte[] Hash(string plainValue) => NodeEnrollmentToken.Hash(plainValue);

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt && Uses < MaxUses;

    /// <summary>Records a use (the atomic count itself happens in the database, so two machines never share the last one).</summary>
    public void Used(DateTimeOffset now)
    {
        Uses++;
        LastUsedAt = now;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public static class EnrollmentTokenErrors
{
    public static DomainError NotFound { get; } = new("enrollment_token.not_found", "The enrollment token does not exist.");

    public static DomainError NameInvalid { get; } =
        new("enrollment_token.name_invalid", $"A name is one line of 1 to {ReusableEnrollmentToken.MaxNameLength} characters.");

    public static DomainError UsesInvalid { get; } =
        new("enrollment_token.uses_invalid", $"A token enrolls 1 to {ReusableEnrollmentToken.MaxUsesLimit} machines.");

    public static DomainError LifetimeInvalid { get; } =
        new("enrollment_token.lifetime_invalid", $"A token is valid for 1 hour to {ReusableEnrollmentToken.MaxLifetimeDays} days.");

    public static DomainError TooMany { get; } = new("enrollment_token.too_many", "The panel has at most 50 enrollment tokens that can still be used.");
}
