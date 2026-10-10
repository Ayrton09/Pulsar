using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Application.Nodes;

/// <param name="TokenPrefix">The first characters of the token, to recognize it in a deployment script.</param>
/// <param name="Usable">Not revoked, not expired and not used up.</param>
public sealed record EnrollmentTokenView(
    Guid Id,
    string Name,
    string TokenPrefix,
    int MaxUses,
    int Uses,
    bool RequireApproval,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool Usable);

/// <summary>Shown once: the token and the panel key the agents pin (as for a single-use token).</summary>
/// <param name="AgentPackage">The agent's MSI of this panel's release, for the deployment command.</param>
public sealed record CreatedEnrollmentToken(EnrollmentTokenView Token, string Value, string PanelKeyFingerprint, string AgentPackage);

/// <summary>
/// Reusable enrollment tokens (Phase 14, docs/architecture/03, "Bulk enrollment"): each use enrolls a new node, so one
/// command or one MSI deployment enrolls many machines. Managed with node.credentials.manage, held globally.
/// </summary>
public sealed class EnrollmentTokens(IPulsarDbContext db, IPanelKeyRing keys, IRequestContext request, IAuditLog audit, TimeProvider clock)
{
    /// <summary>How many tokens that can still be used the panel keeps at once.</summary>
    public const int MaxUsable = 50;

    public async Task<IReadOnlyList<EnrollmentTokenView>> ListAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var tokens = await db.ReusableEnrollmentTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(cancellationToken);
        return [.. tokens.Select(t => View(t, now))];
    }

    public async Task<Result<CreatedEnrollmentToken>> CreateAsync(string name, int maxUses, TimeSpan lifetime, bool requireApproval,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var usable = await db.ReusableEnrollmentTokens.CountAsync(t => t.RevokedAt == null && t.ExpiresAt > now && t.Uses < t.MaxUses, cancellationToken);
        if (usable >= MaxUsable)
        {
            return EnrollmentTokenErrors.TooMany;
        }

        var issued = ReusableEnrollmentToken.Issue(name, maxUses, lifetime, requireApproval, request.UserId, now);
        if (!issued.IsSuccess)
        {
            return issued.Error!;
        }

        var (token, value) = issued.Value;
        db.ReusableEnrollmentTokens.Add(token);
        audit.Record(AuditActions.EnrollmentToken.Created, metadata: new
        {
            name = token.Name,
            prefix = token.TokenPrefix,
            maxUses = token.MaxUses,
            expiresAt = token.ExpiresAt,
            requireApproval = token.RequireApproval,
        }).WithTarget("enrollment_token", token.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        var panelKey = (await keys.GetPublicKeysAsync(cancellationToken))[0];
        return new CreatedEnrollmentToken(View(token, now), value, panelKey.KeyId, AgentPackage);
    }

    /// <summary>The name the release gives the agent's MSI (scripts/release/Build-Release.ps1): the panel's own version.</summary>
    public static string AgentPackage { get; } = $"pulsar-agent-{ReleaseVersion()}-windows-x64.msi";

    private static string ReleaseVersion()
    {
        var version = typeof(EnrollmentTokens).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "1.0.0";
        return version.Split('+')[0];
    }

    /// <summary>No machine can enroll with it anymore; the nodes it created stay.</summary>
    public async Task<Result> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await db.ReusableEnrollmentTokens.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (token is null)
        {
            return EnrollmentTokenErrors.NotFound;
        }

        if (token.RevokedAt is null)
        {
            token.Revoke(clock.GetUtcNow());
            audit.Record(AuditActions.EnrollmentToken.Revoked, metadata: new { name = token.Name, prefix = token.TokenPrefix, uses = token.Uses })
                .WithTarget("enrollment_token", token.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    private static EnrollmentTokenView View(ReusableEnrollmentToken token, DateTimeOffset now) => new(token.Id, token.Name, token.TokenPrefix,
        token.MaxUses, token.Uses, token.RequireApproval, token.CreatedAt, token.ExpiresAt, token.LastUsedAt, token.RevokedAt, token.IsUsable(now));
}
