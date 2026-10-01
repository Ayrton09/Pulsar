using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Access;

/// <summary>
/// Single source of truth for "may this user do X here", used by REST endpoints and (later) hub methods alike.
/// Grants are cached per user for a short time and invalidated explicitly when roles or assignments change
/// (valid because v1 runs a single panel instance, docs/architecture/02).
/// </summary>
public interface IPermissionEvaluator
{
    Task<EffectivePermissions> GetAsync(UserId user, CancellationToken cancellationToken);

    async Task<bool> HasAsync(UserId user, string permission, AccessTarget target, CancellationToken cancellationToken) =>
        (await GetAsync(user, cancellationToken)).Has(permission, target);

    /// <summary>Call after changing a user's assignments.</summary>
    void Invalidate(UserId user);

    /// <summary>Call after changing a role (it may be assigned to many users).</summary>
    void InvalidateAll();
}

internal sealed class PermissionEvaluator(IPulsarDbContext db, IMemoryCache cache, PermissionCacheVersion version) : IPermissionEvaluator
{
    private static readonly TimeSpan s_ttl = TimeSpan.FromMinutes(1);

    public async Task<EffectivePermissions> GetAsync(UserId user, CancellationToken cancellationToken)
    {
        var key = (nameof(PermissionEvaluator), user, version.Current);
        if (cache.TryGetValue(key, out EffectivePermissions? cached) && cached is not null)
        {
            return cached;
        }

        var roles = await db.Roles
            .AsNoTracking()
            .Where(r => db.UserRoles.Any(a => a.UserId == user && a.RoleId == r.Id))
            .ToListAsync(cancellationToken);

        var builder = new EffectivePermissions.Builder();
        foreach (var role in roles)
        {
            builder.Add(role, AccessTarget.Global);
        }

        var permissions = builder.Build();
        cache.Set(key, permissions, s_ttl);
        return permissions;
    }

    public void Invalidate(UserId user) => cache.Remove((nameof(PermissionEvaluator), user, version.Current));

    public void InvalidateAll() => version.Increment();
}

/// <summary>Bumping the version makes every cached entry unreachable; they expire on their own.</summary>
internal sealed class PermissionCacheVersion
{
    private long _current;

    public long Current => Interlocked.Read(ref _current);

    public void Increment() => Interlocked.Increment(ref _current);
}
