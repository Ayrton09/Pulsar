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
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(s => s.NodeId);

        builder.HasOne<Node>().WithMany().HasForeignKey(s => s.NodeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GameTemplate>().WithMany().HasForeignKey(s => s.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Operation>().WithMany().HasForeignKey(s => s.ActiveOperationId).OnDelete(DeleteBehavior.SetNull);
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
