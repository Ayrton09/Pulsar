using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Audit;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        // No foreign keys on purpose: entries outlive the users, nodes and servers they mention.
        builder.ToTable("audit_logs", t =>
        {
            t.HasCheckConstraint("ck_audit_logs_outcome", SnakeCaseEnum<AuditOutcome>.CheckConstraint("outcome"));
            t.HasCheckConstraint("ck_audit_logs_actor_type", SnakeCaseEnum<AuditActorType>.CheckConstraint("actor_type"));
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Action).HasMaxLength(AuditEntry.MaxActionLength);
        builder.Property(e => e.Outcome).HasConversion<SnakeCaseEnumConverter<AuditOutcome>>();
        builder.Property(e => e.ActorType).HasConversion<SnakeCaseEnumConverter<AuditActorType>>();
        builder.Property(e => e.ActorDisplay).HasMaxLength(AuditEntry.MaxActorDisplayLength);
        builder.Property(e => e.TargetType).HasMaxLength(AuditEntry.MaxTargetTypeLength);
        builder.Property(e => e.TargetId).HasMaxLength(AuditEntry.MaxTargetIdLength);
        builder.Property(e => e.IpAddress).HasColumnType("inet");
        builder.Property(e => e.UserAgent).HasMaxLength(AuditEntry.MaxUserAgentLength);
        builder.Property(e => e.CorrelationId).HasMaxLength(AuditEntry.MaxCorrelationIdLength);
        builder.Property(e => e.Metadata).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

        builder.HasIndex(e => e.OccurredAt).IsDescending().HasDatabaseName("ix_audit_time");
        builder.HasIndex(e => new { e.ServerId, e.OccurredAt })
            .IsDescending(false, true)
            .HasFilter("server_id IS NOT NULL")
            .HasDatabaseName("ix_audit_server");
        builder.HasIndex(e => new { e.ActorUserId, e.OccurredAt })
            .IsDescending(false, true)
            .HasFilter("actor_user_id IS NOT NULL")
            .HasDatabaseName("ix_audit_actor");
    }
}
