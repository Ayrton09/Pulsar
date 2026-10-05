using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Abstractions;

/// <summary>
/// The panel database as seen by application handlers: LINQ over the aggregates, no repositories
/// (docs/architecture/01, option A). Implemented in Infrastructure with the PostgreSQL provider.
/// </summary>
public interface IPulsarDbContext
{
    DbSet<Role> Roles { get; }

    DbSet<UserRoleAssignment> UserRoles { get; }

    DbSet<NodeAccess> NodeAccess { get; }

    DbSet<ServerAccess> ServerAccess { get; }

    DbSet<ApiKey> ApiKeys { get; }

    DbSet<AuditEntry> AuditLog { get; }

    DbSet<Node> Nodes { get; }

    DbSet<NodeCredential> NodeCredentials { get; }

    DbSet<NodeEnrollmentToken> NodeEnrollmentTokens { get; }

    DbSet<PanelSigningKey> PanelSigningKeys { get; }

    DbSet<GameTemplate> GameTemplates { get; }

    DbSet<Server> Servers { get; }

    DbSet<Operation> Operations { get; }

    DbSet<NodeAllocation> NodeAllocations { get; }

    DbSet<Backup> Backups { get; }

    DbSet<ScheduledTask> ScheduledTasks { get; }

    DbSet<ScheduledTaskRun> ScheduledTaskRuns { get; }

    DbSet<BackupStorage> BackupStorages { get; }

    DbSet<NotificationChannel> NotificationChannels { get; }

    DbSet<NotificationSettings> NotificationSettings { get; }

    DbSet<NotificationDelivery> NotificationDeliveries { get; }

    /// <summary>Transactions and set-based updates (e.g. consuming a token atomically).</summary>
    DatabaseFacade Database { get; }

    /// <summary>Reloading entities after an optimistic concurrency conflict.</summary>
    ChangeTracker ChangeTracker { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
