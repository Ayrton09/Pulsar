using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Nodes;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class NodeConfiguration : IEntityTypeConfiguration<Node>
{
    public void Configure(EntityTypeBuilder<Node> builder)
    {
        builder.ToTable("nodes", t => t.HasCheckConstraint("ck_nodes_status", SnakeCaseEnum<NodeStatus>.CheckConstraint("status")));

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Name).HasMaxLength(Node.MaxNameLength);
        builder.Property(n => n.NormalizedName).HasMaxLength(Node.MaxNameLength);
        builder.HasIndex(n => n.NormalizedName).IsUnique();
        builder.Property(n => n.Description).HasMaxLength(Node.MaxDescriptionLength);
        builder.Property(n => n.PublicHost).HasMaxLength(Node.MaxPublicHostLength);
        builder.Property(n => n.Status).HasConversion<SnakeCaseEnumConverter<NodeStatus>>();
        builder.HasOne<ReusableEnrollmentToken>().WithMany().HasForeignKey(n => n.EnrollmentTokenId).OnDelete(DeleteBehavior.SetNull);
        builder.PrimitiveCollection(n => n.PinnedPanelKeys);
        builder.Property<uint>("Version").IsRowVersion();

        // Last inventory reported by the agent, as plain columns of the node (docs/architecture/02).
        builder.OwnsOne(n => n.Inventory, inventory =>
        {
            inventory.Property(i => i.Hostname).HasColumnName("hostname").HasMaxLength(255);
            inventory.Property(i => i.OsDescription).HasColumnName("os_description").HasMaxLength(255);
            inventory.Property(i => i.OsBuild).HasColumnName("os_build").HasMaxLength(64);
            inventory.Property(i => i.CpuModel).HasColumnName("cpu_model").HasMaxLength(255);
            inventory.Property(i => i.LogicalCores).HasColumnName("cpu_logical_cores");
            inventory.Property(i => i.MemoryTotalBytes).HasColumnName("memory_total_bytes");
            inventory.Property(i => i.ServersRoot).HasColumnName("servers_root").HasMaxLength(1024);
            inventory.Property(i => i.AgentVersion).HasColumnName("agent_version").HasMaxLength(32);
            inventory.Property(i => i.ProtocolVersion).HasColumnName("protocol_version");
        });
    }
}

internal sealed class NodeCredentialConfiguration : IEntityTypeConfiguration<NodeCredential>
{
    public void Configure(EntityTypeBuilder<NodeCredential> builder)
    {
        builder.ToTable("node_credentials", t =>
        {
            t.HasCheckConstraint("ck_node_credentials_status", SnakeCaseEnum<NodeCredentialStatus>.CheckConstraint("status"));
            t.HasCheckConstraint("ck_node_credentials_key_storage", SnakeCaseEnum<NodeKeyStorage>.CheckConstraint("key_storage"));
            t.HasCheckConstraint("ck_node_credentials_algorithm", $"algorithm = '{NodeCredential.Algorithm}'");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property<string>("Algorithm").IsRequired().HasDefaultValue(NodeCredential.Algorithm);
        builder.Property(c => c.Fingerprint).HasMaxLength(80);
        builder.HasIndex(c => c.Fingerprint).IsUnique();
        builder.Property(c => c.Status).HasConversion<SnakeCaseEnumConverter<NodeCredentialStatus>>();
        builder.Property(c => c.KeyStorage).HasConversion<SnakeCaseEnumConverter<NodeKeyStorage>>();
        builder.Property(c => c.RevokeReason).HasMaxLength(NodeCredential.MaxRevokeReasonLength);

        // At most one active credential per node.
        builder.HasIndex(c => c.NodeId).IsUnique().HasFilter("status = 'active'").HasDatabaseName("ux_node_credentials_single_active");

        builder.HasOne<Node>().WithMany().HasForeignKey(c => c.NodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(c => c.RevokedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class NodeEnrollmentTokenConfiguration : IEntityTypeConfiguration<NodeEnrollmentToken>
{
    public void Configure(EntityTypeBuilder<NodeEnrollmentToken> builder)
    {
        builder.ToTable("node_enrollment_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasOne<Node>().WithMany().HasForeignKey(t => t.NodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(t => t.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<NodeCredential>().WithMany().HasForeignKey(t => t.ConsumedByCredentialId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PanelSigningKeyConfiguration : IEntityTypeConfiguration<PanelSigningKey>
{
    public void Configure(EntityTypeBuilder<PanelSigningKey> builder)
    {
        builder.ToTable("panel_signing_keys", t =>
            t.HasCheckConstraint("ck_panel_signing_keys_status", SnakeCaseEnum<PanelSigningKeyStatus>.CheckConstraint("status")));
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.KeyId).HasMaxLength(80);
        builder.HasIndex(k => k.KeyId).IsUnique();
        builder.Property(k => k.Status).HasConversion<SnakeCaseEnumConverter<PanelSigningKeyStatus>>();

        // A single active signing key.
        builder.HasIndex(k => k.Status).IsUnique().HasFilter("status = 'active'").HasDatabaseName("ux_panel_signing_keys_single_active");
    }
}

internal sealed class ReusableEnrollmentTokenConfiguration : IEntityTypeConfiguration<ReusableEnrollmentToken>
{
    public void Configure(EntityTypeBuilder<ReusableEnrollmentToken> builder)
    {
        builder.ToTable("reusable_enrollment_tokens", t =>
        {
            t.HasCheckConstraint("ck_reusable_enrollment_tokens_uses", "uses >= 0 AND uses <= max_uses");
            t.HasCheckConstraint("ck_reusable_enrollment_tokens_max_uses", $"max_uses BETWEEN 1 AND {ReusableEnrollmentToken.MaxUsesLimit}");
        });
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(ReusableEnrollmentToken.MaxNameLength);
        builder.Property(t => t.TokenPrefix).HasMaxLength(32);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(t => t.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}
