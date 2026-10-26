using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Scheduling;
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
            t.HasCheckConstraint("ck_servers_database_limit", $"database_limit BETWEEN 0 AND {Server.MaxDatabaseLimit}");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.ShortId).HasMaxLength(12);
        builder.HasIndex(s => s.ShortId).IsUnique();
        builder.Property(s => s.Name).HasMaxLength(Server.MaxNameLength);
        builder.Property(s => s.Description).HasMaxLength(Server.MaxDescriptionLength);
        builder.Property(s => s.GameVersion).HasMaxLength(Server.MaxGameVersionLength);
        builder.Property(s => s.InstallState).HasConversion<SnakeCaseEnumConverter<InstallState>>();
        builder.Property(s => s.RuntimeState).HasConversion<SnakeCaseEnumConverter<ServerRuntimeState>>();
        builder.Property(s => s.DesiredState).HasConversion<SnakeCaseEnumConverter<DesiredState>>();
        builder.Property(s => s.LastStateReason).HasConversion<SnakeCaseEnumConverter<ServerStateReason>>();
        builder.Property(s => s.ActiveOperationType).HasConversion<SnakeCaseEnumConverter<OperationType>>();
        builder.Property<uint>("Version").IsRowVersion();
        // Existing servers get the default; 0 (no databases) is a value of its own, not "unset".
        builder.Property(s => s.DatabaseLimit).HasDefaultValue(Server.DefaultDatabaseLimit).HasSentinel(-1);
        builder.HasIndex(s => s.NodeId);
        builder.HasOne<SteamAccount>().WithMany().HasForeignKey(s => s.SteamAccountId).OnDelete(DeleteBehavior.Restrict);
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
            t.HasCheckConstraint("ck_backups_copy_mode", SnakeCaseEnum<BackupCopyMode>.CheckConstraint("copy_mode"));
            t.HasCheckConstraint("ck_backups_game_save", SnakeCaseEnum<GameSaveResult>.CheckConstraint("game_save"));
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
        builder.Property(b => b.CopyMode).HasConversion<SnakeCaseEnumConverter<BackupCopyMode>>();
        builder.Property(b => b.SnapshotError).HasMaxLength(Backup.MaxSnapshotErrorLength);
        builder.Property(b => b.GameSave).HasConversion<SnakeCaseEnumConverter<GameSaveResult>>();
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

/// <summary>Addons installed on servers (docs/architecture/02, "Addons"): gone with their server.</summary>
internal sealed class ServerAddonConfiguration : IEntityTypeConfiguration<ServerAddon>
{
    public void Configure(EntityTypeBuilder<ServerAddon> builder)
    {
        builder.ToTable("server_addons");
        builder.HasKey(a => new { a.ServerId, a.Key });
        builder.Property(a => a.Key).HasMaxLength(ServerAddon.MaxKeyLength);
        builder.Property(a => a.Release).HasMaxLength(ServerAddon.MaxReleaseLength);
        builder.HasOne<Server>().WithMany().HasForeignKey(a => a.ServerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ServerModConfiguration : IEntityTypeConfiguration<ServerMod>
{
    public void Configure(EntityTypeBuilder<ServerMod> builder)
    {
        builder.ToTable("server_mods", t =>
        {
            t.HasCheckConstraint("ck_server_mods_source", SnakeCaseEnum<ModSource>.CheckConstraint("source"));
            t.HasCheckConstraint("ck_server_mods_state", SnakeCaseEnum<ServerModState>.CheckConstraint("state"));
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Source).HasConversion<SnakeCaseEnumConverter<ModSource>>();
        builder.Property(m => m.State).HasConversion<SnakeCaseEnumConverter<ServerModState>>();
        builder.Property(m => m.ProjectId).HasMaxLength(ServerMod.MaxIdLength);
        builder.Property(m => m.VersionId).HasMaxLength(ServerMod.MaxIdLength);
        builder.Property(m => m.Name).HasMaxLength(ServerMod.MaxNameLength);
        builder.Property(m => m.VersionNumber).HasMaxLength(ServerMod.MaxVersionLength);
        builder.Property(m => m.Path).HasMaxLength(ServerMod.MaxPathLength);
        builder.Property(m => m.Hash).HasMaxLength(128);
        builder.Property(m => m.Requires).HasColumnType("jsonb").HasConversion(
            requires => JsonSerializer.Serialize(requires, (JsonSerializerOptions?)null),
            json => JsonSerializer.Deserialize<List<string>>(json, (JsonSerializerOptions?)null) ?? new List<string>(),
            new ValueComparer<IReadOnlyList<string>>((a, b) => a!.SequenceEqual(b!), list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                list => list.ToList()));
        // An operation's report and a user's view settle the same rows: the loser reads them again.
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(m => new { m.ServerId, m.ProjectId });
        builder.HasIndex(m => m.OperationId).HasFilter("operation_id IS NOT NULL");

        builder.HasOne<Server>().WithMany().HasForeignKey(m => m.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Operation>().WithMany().HasForeignKey(m => m.OperationId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ServerModpackConfiguration : IEntityTypeConfiguration<ServerModpack>
{
    public void Configure(EntityTypeBuilder<ServerModpack> builder)
    {
        builder.ToTable("server_modpacks", t =>
        {
            t.HasCheckConstraint("ck_server_modpacks_source", SnakeCaseEnum<ModSource>.CheckConstraint("source"));
            t.HasCheckConstraint("ck_server_modpacks_state", SnakeCaseEnum<ServerModpackState>.CheckConstraint("state"));
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Source).HasConversion<SnakeCaseEnumConverter<ModSource>>();
        builder.Property(m => m.State).HasConversion<SnakeCaseEnumConverter<ServerModpackState>>();
        builder.Property(m => m.ProjectId).HasMaxLength(ServerModpack.MaxIdLength);
        builder.Property(m => m.VersionId).HasMaxLength(ServerModpack.MaxIdLength);
        builder.Property(m => m.Name).HasMaxLength(ServerModpack.MaxNameLength);
        builder.Property(m => m.VersionNumber).HasMaxLength(ServerModpack.MaxVersionLength);
        builder.Property(m => m.ServerType).HasMaxLength(ServerModpack.MaxTypeLength);
        builder.Property(m => m.Loader).HasMaxLength(ServerModpack.MaxTypeLength);
        builder.Property(m => m.GameVersion).HasMaxLength(ServerModpack.MaxVersionLength);
        builder.Property(m => m.LoaderVersion).HasMaxLength(ServerModpack.MaxVersionLength);
        builder.Property<uint>("Version").IsRowVersion();
        // One installed pack, and one on its way, per server (kept so by the panel: a unique index would order the
        // settling's deletion and update by chance).
        builder.HasIndex(m => m.ServerId);
        builder.HasIndex(m => m.OperationId).HasFilter("operation_id IS NOT NULL");

        builder.HasOne<Server>().WithMany().HasForeignKey(m => m.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Operation>().WithMany().HasForeignKey(m => m.OperationId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ModCatalogSettingsConfiguration : IEntityTypeConfiguration<ModCatalogSettings>
{
    public void Configure(EntityTypeBuilder<ModCatalogSettings> builder)
    {
        builder.ToTable("mod_catalog_settings", t => t.HasCheckConstraint("ck_mod_catalog_settings_singleton", $"id = {ModCatalogSettings.SingletonId}"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class SteamAccountConfiguration : IEntityTypeConfiguration<SteamAccount>
{
    public void Configure(EntityTypeBuilder<SteamAccount> builder)
    {
        builder.ToTable("steam_accounts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Name).HasMaxLength(SteamAccount.MaxNameLength);
        builder.HasIndex(a => a.Name).IsUnique();
        builder.Property(a => a.UserName).HasMaxLength(SteamAccount.MaxUserNameLength);
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class SftpSettingsConfiguration : IEntityTypeConfiguration<SftpSettings>
{
    public void Configure(EntityTypeBuilder<SftpSettings> builder)
    {
        builder.ToTable("sftp_settings", t =>
        {
            t.HasCheckConstraint("ck_sftp_settings_singleton", $"id = {SftpSettings.SingletonId}");
            t.HasCheckConstraint("ck_sftp_settings_port", "port BETWEEN 1 AND 65535");
        });
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.PublicHost).HasMaxLength(SftpSettings.MaxHostLength);
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class GameUpdateWatchConfiguration : IEntityTypeConfiguration<GameUpdateWatch>
{
    public void Configure(EntityTypeBuilder<GameUpdateWatch> builder)
    {
        builder.ToTable("game_update_watches", t =>
        {
            t.HasCheckConstraint("ck_game_update_watches_policy", SnakeCaseEnum<GameUpdatePolicy>.CheckConstraint("policy"));
            t.HasCheckConstraint("ck_game_update_watches_warning_minutes", $"warning_minutes BETWEEN 0 AND {GameUpdateWatch.MaxWarningMinutes}");
        });

        builder.HasKey(w => w.ServerId);
        builder.Property(w => w.ServerId).ValueGeneratedNever();
        builder.Property(w => w.Policy).HasConversion<SnakeCaseEnumConverter<GameUpdatePolicy>>();
        builder.Property(w => w.WarningMessage).HasMaxLength(GameUpdateWatch.MaxWarningMessageLength);
        builder.Property(w => w.Installed).HasMaxLength(GameUpdateWatch.MaxBuildLength);
        builder.Property(w => w.Latest).HasMaxLength(GameUpdateWatch.MaxBuildLength);
        builder.Property(w => w.NotifiedLatest).HasMaxLength(GameUpdateWatch.MaxBuildLength);
        builder.Property(w => w.AttemptedLatest).HasMaxLength(GameUpdateWatch.MaxBuildLength);
        builder.Property(w => w.CheckErrorCode).HasMaxLength(GameUpdateWatch.MaxErrorCodeLength);
        builder.Property(w => w.CheckError).HasMaxLength(GameUpdateWatch.MaxMessageLength);
        builder.Ignore(w => w.AutoUpdateDue);
        // Checks (the pass, the node's answer) and users (settings) can race: the loser reads it again.
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(w => w.NextCheckAt).HasFilter("policy <> 'off'").HasDatabaseName("ix_game_update_watches_due");

        builder.HasOne<Server>().WithOne().HasForeignKey<GameUpdateWatch>(w => w.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ServerSequence>().WithMany().HasForeignKey(w => w.SequenceId).OnDelete(DeleteBehavior.SetNull);
    }
}

