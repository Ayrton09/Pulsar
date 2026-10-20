using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Access;
using Pulsar.Domain.Appearance;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Certificates;
using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;
using Pulsar.Domain.Metrics;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;
using Pulsar.Domain.Updates;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>The panel database (PostgreSQL only). Schema conventions are described in docs/architecture/02.</summary>
public sealed class PulsarDbContext(DbContextOptions<PulsarDbContext> options)
    : IdentityUserContext<PulsarUser, UserId>(options), IPulsarDbContext
{
    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserRoleAssignment> UserRoles => Set<UserRoleAssignment>();

    public DbSet<NodeAccess> NodeAccess => Set<NodeAccess>();

    public DbSet<ServerAccess> ServerAccess => Set<ServerAccess>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<SshKey> SshKeys => Set<SshKey>();

    public DbSet<ServerAddon> ServerAddons => Set<ServerAddon>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    public DbSet<Node> Nodes => Set<Node>();

    public DbSet<NodeCredential> NodeCredentials => Set<NodeCredential>();

    public DbSet<NodeEnrollmentToken> NodeEnrollmentTokens => Set<NodeEnrollmentToken>();

    public DbSet<ReusableEnrollmentToken> ReusableEnrollmentTokens => Set<ReusableEnrollmentToken>();

    public DbSet<PanelSigningKey> PanelSigningKeys => Set<PanelSigningKey>();

    public DbSet<AgentTransportSettings> AgentTransportSettings => Set<AgentTransportSettings>();

    public DbSet<SftpSettings> SftpSettings => Set<SftpSettings>();

    public DbSet<PublicIpCertificateSettings> PublicIpCertificateSettings => Set<PublicIpCertificateSettings>();

    public DbSet<AppearanceSettings> AppearanceSettings => Set<AppearanceSettings>();

    public DbSet<UpdateState> UpdateState => Set<UpdateState>();

    public DbSet<NodeUpdate> NodeUpdates => Set<NodeUpdate>();

    public DbSet<GameTemplate> GameTemplates => Set<GameTemplate>();

    public DbSet<Server> Servers => Set<Server>();

    public DbSet<Operation> Operations => Set<Operation>();

    public DbSet<NodeAllocation> NodeAllocations => Set<NodeAllocation>();

    public DbSet<Backup> Backups => Set<Backup>();

    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();

    public DbSet<ScheduledTaskRun> ScheduledTaskRuns => Set<ScheduledTaskRun>();

    public DbSet<BackupStorage> BackupStorages => Set<BackupStorage>();

    public DbSet<SteamAccount> SteamAccounts => Set<SteamAccount>();

    public DbSet<DatabaseHost> DatabaseHosts => Set<DatabaseHost>();

    public DbSet<ServerDatabase> ServerDatabases => Set<ServerDatabase>();

    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();

    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    internal DbSet<ServerMetricBucket> ServerMetrics => Set<ServerMetricBucket>();

    internal DbSet<NodeMetricBucket> NodeMetrics => Set<NodeMetricBucket>();

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
        configurationBuilder.Properties<BackupStorageId>().HaveConversion<BackupStorageIdConverter>();
        configurationBuilder.Properties<ApiKeyId>().HaveConversion<ApiKeyIdConverter>();
        configurationBuilder.Properties<SshKeyId>().HaveConversion<SshKeyIdConverter>();
        configurationBuilder.Properties<SteamAccountId>().HaveConversion<SteamAccountIdConverter>();
        configurationBuilder.Properties<DatabaseHostId>().HaveConversion<DatabaseHostIdConverter>();
        configurationBuilder.Properties<ServerDatabaseId>().HaveConversion<ServerDatabaseIdConverter>();
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
