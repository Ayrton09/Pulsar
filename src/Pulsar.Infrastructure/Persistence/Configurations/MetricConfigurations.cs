using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Metrics;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Infrastructure.Persistence.Configurations;

// Written and read with SQL by MetricHistoryStore; mapped here for the schema (and its migrations) only.

internal sealed class ServerMetricBucketConfiguration : IEntityTypeConfiguration<ServerMetricBucket>
{
    public void Configure(EntityTypeBuilder<ServerMetricBucket> builder)
    {
        builder.ToTable("server_metrics", t =>
        {
            t.HasCheckConstraint("ck_server_metrics_resolution", "resolution IN (1, 60)");
            t.HasCheckConstraint("ck_server_metrics_samples", "samples > 0 AND player_samples BETWEEN 0 AND samples");
        });

        builder.HasKey(b => new { b.ServerId, b.Resolution, b.At });
        builder.Property(b => b.Resolution).HasConversion<short>();
        // Deleting a server deletes its history.
        builder.HasOne<Server>().WithMany().HasForeignKey(b => b.ServerId).OnDelete(DeleteBehavior.Cascade);
        // Pruning goes by age, for every server at once.
        builder.HasIndex(b => new { b.Resolution, b.At });
    }
}

internal sealed class NodeMetricBucketConfiguration : IEntityTypeConfiguration<NodeMetricBucket>
{
    public void Configure(EntityTypeBuilder<NodeMetricBucket> builder)
    {
        builder.ToTable("node_metrics", t =>
        {
            t.HasCheckConstraint("ck_node_metrics_resolution", "resolution IN (1, 60)");
            t.HasCheckConstraint("ck_node_metrics_samples", "samples > 0");
        });

        builder.HasKey(b => new { b.NodeId, b.Resolution, b.At });
        builder.Property(b => b.Resolution).HasConversion<short>();
        builder.HasOne<Node>().WithMany().HasForeignKey(b => b.NodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(b => new { b.Resolution, b.At });
    }
}
