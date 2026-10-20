using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Updates;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class UpdateStateConfiguration : IEntityTypeConfiguration<UpdateState>
{
    public void Configure(EntityTypeBuilder<UpdateState> builder)
    {
        builder.ToTable("update_state", t =>
            t.HasCheckConstraint("ck_update_state_singleton", $"id = {UpdateState.SingletonId}"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.CheckError).HasMaxLength(UpdateState.MaxErrorLength);
        builder.Property(s => s.LatestVersion).HasMaxLength(UpdateState.MaxVersionLength);
        builder.Property(s => s.LatestManifest).HasMaxLength(UpdateState.MaxManifestLength);
        builder.Property(s => s.LatestManifestSha256).HasMaxLength(64);
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class NodeUpdateConfiguration : IEntityTypeConfiguration<NodeUpdate>
{
    public void Configure(EntityTypeBuilder<NodeUpdate> builder)
    {
        builder.ToTable("node_updates");
        builder.HasKey(u => u.NodeId);
        builder.Property(u => u.Status).HasMaxLength(NodeUpdate.MaxStatusLength);
        builder.Property(u => u.FromVersion).HasMaxLength(NodeUpdate.MaxVersionLength);
        builder.Property(u => u.ToVersion).HasMaxLength(NodeUpdate.MaxVersionLength);
        builder.Property(u => u.Error).HasMaxLength(NodeUpdate.MaxErrorLength);
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasOne<Node>().WithOne().HasForeignKey<NodeUpdate>(u => u.NodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(u => u.RequestedBy).OnDelete(DeleteBehavior.SetNull);
    }
}
