using Microsoft.EntityFrameworkCore;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;

namespace Pulsar.Application.Abstractions;

/// <summary>
/// The panel database as seen by application handlers: LINQ over the aggregates, no repositories
/// (docs/architecture/01, option A). Implemented in Infrastructure with the PostgreSQL provider.
/// </summary>
public interface IPulsarDbContext
{
    DbSet<Role> Roles { get; }

    DbSet<UserRoleAssignment> UserRoles { get; }

    DbSet<AuditEntry> AuditLog { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
