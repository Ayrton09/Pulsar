using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Nodes;

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

    DbSet<Node> Nodes { get; }

    DbSet<NodeCredential> NodeCredentials { get; }

    DbSet<NodeEnrollmentToken> NodeEnrollmentTokens { get; }

    DbSet<PanelSigningKey> PanelSigningKeys { get; }

    /// <summary>Transactions and set-based updates (e.g. consuming a token atomically).</summary>
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
