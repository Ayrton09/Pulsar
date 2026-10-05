using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class GameTemplateConfiguration : IEntityTypeConfiguration<GameTemplate>
{
    public void Configure(EntityTypeBuilder<GameTemplate> builder)
    {
        builder.ToTable("game_templates", t => t.HasCheckConstraint("ck_game_templates_source", SnakeCaseEnum<TemplateSource>.CheckConstraint("source")));
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Key).HasMaxLength(64);
        builder.Property(t => t.Version).HasMaxLength(32);
        builder.Property(t => t.Name).HasMaxLength(128);
        builder.Property(t => t.Description).HasMaxLength(1024);
        builder.Property(t => t.Source).HasConversion<SnakeCaseEnumConverter<TemplateSource>>();
        // Text, not jsonb: jsonb would not keep the exact bytes the hash identifies (docs/architecture/02).
        builder.Property(t => t.Content).HasColumnType("text");
        builder.HasIndex(t => new { t.Key, t.Version }).IsUnique();
    }
}

internal sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        builder.ToTable("servers", t =>
        {
            t.HasCheckConstraint("ck_servers_install_state", SnakeCaseEnum<InstallState>.CheckConstraint("install_state"));
            t.HasCheckConstraint("ck_servers_runtime_state", SnakeCaseEnum<ServerRuntimeState>.CheckConstraint("runtime_state"));
            t.HasCheckConstraint("ck_servers_desired_state", SnakeCaseEnum<DesiredState>.CheckConstraint("desired_state"));
            t.HasCheckConstraint("ck_servers_last_state_reason", SnakeCaseEnum<ServerStateReason>.CheckConstraint("last_state_reason"));
            t.HasCheckConstraint("ck_servers_active_operation_type", SnakeCaseEnum<OperationType>.CheckConstraint("active_operation_type"));
            t.HasCheckConstraint("ck_servers_backup_limit", $"backup_limit BETWEEN 1 AND {Server.MaxBackupLimit}");
            t.HasCheckConstraint("ck_servers_cpu_limit_percent", "cpu_limit_percent BETWEEN 1 AND 100");
            t.HasCheckConstraint("ck_servers_memory_limit_mb", $"memory_limit_mb BETWEEN {Server.MinMemoryLimitMb} AND {Server.MaxMemoryLimitMb}");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.ShortId).HasMaxLength(12);
        builder.HasIndex(s => s.ShortId).IsUnique();
        builder.Property(s => s.Name).HasMaxLength(Server.MaxNameLength);
        builder.Property(s => s.Description).HasMaxLength(Server.MaxDescriptionLength);
        builder.Property(s => s.InstallState).HasConversion<SnakeCaseEnumConverter<InstallState>>();
        builder.Property(s => s.RuntimeState).HasConversion<SnakeCaseEnumConverter<ServerRuntimeState>>();
        builder.Property(s => s.DesiredState).HasConversion<SnakeCaseEnumConverter<DesiredState>>();
        builder.Property(s => s.LastStateReason).HasConversion<SnakeCaseEnumConverter<ServerStateReason>>();
        builder.Property(s => s.ActiveOperationType).HasConversion<SnakeCaseEnumConverter<OperationType>>();
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(s => s.NodeId);
        builder.Property(s => s.ExtraArguments).HasColumnType("text[]").HasDefaultValueSql("'{}'");
        builder.HasMany(s => s.Variables).WithOne().HasForeignKey(v => v.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Variables).HasField("_variables").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne<BackupStorage>().WithMany().HasForeignKey(s => s.BackupStorageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Node>().WithMany().HasForeignKey(s => s.NodeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GameTemplate>().WithMany().HasForeignKey(s => s.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Operation>().WithMany().HasForeignKey(s => s.ActiveOperationId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ServerVariableConfiguration : IEntityTypeConfiguration<ServerVariable>
{
    public void Configure(EntityTypeBuilder<ServerVariable> builder)
    {
        builder.ToTable("server_variables",
            t => t.HasCheckConstraint("ck_server_variables_one_value", "(value IS NULL) <> (protected_value IS NULL)"));
        builder.HasKey(v => new { v.ServerId, v.Key });
        builder.Property(v => v.Key).HasMaxLength(64);
        builder.Property(v => v.Value).HasMaxLength(ServerVariable.MaxValueLength);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(v => v.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class NodeAllocationConfiguration : IEntityTypeConfiguration<NodeAllocation>
{
    public void Configure(EntityTypeBuilder<NodeAllocation> builder)
    {
        builder.ToTable("node_allocations", t =>
        {
            t.HasCheckConstraint("ck_node_allocations_port", "port BETWEEN 1 AND 65535");
            t.HasCheckConstraint("ck_node_allocations_assignment", "(server_id IS NULL) = (port_name IS NULL)");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        // Canonical IPv4 text ("0.0.0.0" for every interface); overlaps with 0.0.0.0 are checked by the application.
        builder.Property(a => a.IpAddress).HasMaxLength(15);
        builder.Property(a => a.PortName).HasMaxLength(NodeAllocation.MaxPortNameLength);
        builder.Property(a => a.Notes).HasMaxLength(NodeAllocation.MaxNotesLength);
        builder.HasIndex(a => new { a.NodeId, a.IpAddress, a.Port }).IsUnique();
        builder.HasIndex(a => new { a.ServerId, a.PortName }).IsUnique();
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasOne<Node>().WithMany().HasForeignKey(a => a.NodeId).OnDelete(DeleteBehavior.Cascade);
        // Released by the application before a server row goes (the assignment check would refuse SET NULL).
        builder.HasOne<Server>().WithMany().HasForeignKey(a => a.ServerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OperationConfiguration : IEntityTypeConfiguration<Operation>
{
    public void Configure(EntityTypeBuilder<Operation> builder)
    {
        builder.ToTable("operations", t =>
        {
            t.HasCheckConstraint("ck_operations_type", SnakeCaseEnum<OperationType>.CheckConstraint("type"));
            t.HasCheckConstraint("ck_operations_status", SnakeCaseEnum<OperationStatus>.CheckConstraint("status"));
            t.HasCheckConstraint("ck_operations_requested_via", SnakeCaseEnum<OperationRequester>.CheckConstraint("requested_via"));
        });

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.Type).HasConversion<SnakeCaseEnumConverter<OperationType>>();
        builder.Property(o => o.Status).HasConversion<SnakeCaseEnumConverter<OperationStatus>>();
        builder.Property(o => o.RequestedVia).HasConversion<SnakeCaseEnumConverter<OperationRequester>>();
        builder.Property(o => o.ProgressStage).HasMaxLength(Operation.MaxStageLength);
        builder.Property(o => o.ProgressPercent).HasPrecision(5, 2);
        builder.Property(o => o.ErrorCode).HasMaxLength(64);
        builder.Property(o => o.ErrorMessage).HasMaxLength(Operation.MaxErrorMessageLength);
        // The agent's result and the panel's own updates (accepted, failed to send) can race: the loser retries or gives way.
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(o => new { o.ServerId, o.CreatedAt });
        builder.HasIndex(o => o.NodeId).HasFilter("status IN ('queued', 'running')").HasDatabaseName("ix_operations_active");

        builder.HasOne<Node>().WithMany().HasForeignKey(o => o.NodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Server>().WithMany().HasForeignKey(o => o.ServerId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(o => o.RequestedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class BackupConfiguration : IEntityTypeConfiguration<Backup>
{
    public void Configure(EntityTypeBuilder<Backup> builder)
    {
        builder.ToTable("backups", t =>
        {
            t.HasCheckConstraint("ck_backups_status", SnakeCaseEnum<BackupStatus>.CheckConstraint("status"));
            t.HasCheckConstraint("ck_backups_created_via", SnakeCaseEnum<OperationRequester>.CheckConstraint("created_via"));
            t.HasCheckConstraint("ck_backups_remote_status", SnakeCaseEnum<RemoteCopyStatus>.CheckConstraint("remote_status"));
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Name).HasMaxLength(Backup.MaxNameLength);
        builder.Property(b => b.Status).HasConversion<SnakeCaseEnumConverter<BackupStatus>>();
        builder.Property(b => b.CreatedVia).HasConversion<SnakeCaseEnumConverter<OperationRequester>>();
        builder.Property(b => b.ErrorMessage).HasMaxLength(Backup.MaxErrorMessageLength);
        builder.Property(b => b.RemoteStatus).HasConversion<SnakeCaseEnumConverter<RemoteCopyStatus>>().HasDefaultValue(RemoteCopyStatus.None)
            .HasSentinel(RemoteCopyStatus.None);
        builder.Property(b => b.RemoteKey).HasMaxLength(512);
        builder.Property(b => b.RemoteError).HasMaxLength(Backup.MaxErrorMessageLength);
        builder.Ignore(b => b.Duration);
        // The agent's result, retention and users (locking, deleting) can race: the loser retries or gives way.
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(b => new { b.ServerId, b.CreatedAt });
        builder.HasIndex(b => b.OperationId);

        builder.HasOne<Server>().WithMany().HasForeignKey(b => b.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Node>().WithMany().HasForeignKey(b => b.NodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Operation>().WithMany().HasForeignKey(b => b.OperationId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(b => b.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        // A storage holding copies cannot be deleted (the application says so first; this is the backstop).
        builder.HasOne<BackupStorage>().WithMany().HasForeignKey(b => b.StorageId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BackupStorageConfiguration : IEntityTypeConfiguration<BackupStorage>
{
    public void Configure(EntityTypeBuilder<BackupStorage> builder)
    {
        builder.ToTable("backup_storages");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(BackupStorage.MaxNameLength);
        builder.Property(s => s.Endpoint).HasMaxLength(BackupStorage.MaxEndpointLength);
        builder.Property(s => s.Region).HasMaxLength(BackupStorage.MaxRegionLength);
        builder.Property(s => s.Bucket).HasMaxLength(63);
        builder.Property(s => s.Prefix).HasMaxLength(BackupStorage.MaxPrefixLength);
        builder.Property(s => s.AccessKeyId).HasMaxLength(BackupStorage.MaxAccessKeyLength);
        builder.Property<uint>("Version").IsRowVersion();
    }
}
