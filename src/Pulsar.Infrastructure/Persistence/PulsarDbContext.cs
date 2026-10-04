using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>The panel database (PostgreSQL only). Schema conventions are described in docs/architecture/02.</summary>
public sealed class PulsarDbContext(DbContextOptions<PulsarDbContext> options)
    : IdentityUserContext<PulsarUser, UserId>(options), IPulsarDbContext
{
    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserRoleAssignment> UserRoles => Set<UserRoleAssignment>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    public DbSet<Node> Nodes => Set<Node>();

    public DbSet<NodeCredential> NodeCredentials => Set<NodeCredential>();

    public DbSet<NodeEnrollmentToken> NodeEnrollmentTokens => Set<NodeEnrollmentToken>();

    public DbSet<PanelSigningKey> PanelSigningKeys => Set<PanelSigningKey>();

    public DbSet<GameTemplate> GameTemplates => Set<GameTemplate>();

    public DbSet<Server> Servers => Set<Server>();

    public DbSet<Operation> Operations => Set<Operation>();

    public DbSet<NodeAllocation> NodeAllocations => Set<NodeAllocation>();

    public DbSet<Backup> Backups => Set<Backup>();

    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();

    public DbSet<ScheduledTaskRun> ScheduledTaskRuns => Set<ScheduledTaskRun>();

    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();

    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();
        configurationBuilder.Properties<RoleId>().HaveConversion<RoleIdConverter>();
        configurationBuilder.Properties<NodeId>().HaveConversion<NodeIdConverter>();
        configurationBuilder.Properties<ServerId>().HaveConversion<ServerIdConverter>();
        configurationBuilder.Properties<TemplateId>().HaveConversion<TemplateIdConverter>();
        configurationBuilder.Properties<OperationId>().HaveConversion<OperationIdConverter>();
        configurationBuilder.Properties<AllocationId>().HaveConversion<AllocationIdConverter>();
        configurationBuilder.Properties<BackupId>().HaveConversion<BackupIdConverter>();
        configurationBuilder.Properties<ScheduledTaskId>().HaveConversion<ScheduledTaskIdConverter>();
        configurationBuilder.Properties<ScheduledRunId>().HaveConversion<ScheduledRunIdConverter>();
        configurationBuilder.Properties<NotificationChannelId>().HaveConversion<NotificationChannelIdConverter>();
        configurationBuilder.Properties<NotificationDeliveryId>().HaveConversion<NotificationDeliveryIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Identity tables keep Identity's shape under the project's names.
        builder.Entity<PulsarUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.Id).ValueGeneratedNever();
            user.Property(u => u.UserName).HasMaxLength(PulsarUser.MaxUserNameLength);
            user.Property(u => u.NormalizedUserName).HasMaxLength(PulsarUser.MaxUserNameLength);
            user.Property(u => u.DisplayName).HasMaxLength(PulsarUser.MaxDisplayNameLength);
            user.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
        });
        builder.Entity<IdentityUserClaim<UserId>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<UserId>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<UserId>>().ToTable("user_tokens");

        builder.ApplyConfigurationsFromAssembly(typeof(PulsarDbContext).Assembly);
    }
}
