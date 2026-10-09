using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Databases;
using Pulsar.Domain.Servers;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class DatabaseHostConfiguration : IEntityTypeConfiguration<DatabaseHost>
{
    public void Configure(EntityTypeBuilder<DatabaseHost> builder)
    {
        builder.ToTable("database_hosts", t =>
        {
            t.HasCheckConstraint("ck_database_hosts_port", "port BETWEEN 1 AND 65535");
            t.HasCheckConstraint("ck_database_hosts_server_port", "server_port IS NULL OR server_port BETWEEN 1 AND 65535");
        });

        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.Name).HasMaxLength(DatabaseHost.MaxNameLength);
        builder.HasIndex(h => h.Name).IsUnique();
        builder.Property(h => h.Host).HasMaxLength(DatabaseHost.MaxHostLength);
        builder.Property(h => h.UserName).HasMaxLength(DatabaseHost.MaxUserNameLength);
        builder.Property(h => h.ServerAddress).HasMaxLength(DatabaseHost.MaxHostLength);
        builder.Property(h => h.UserHost).HasMaxLength(DatabaseHost.MaxHostLength);
        builder.PrimitiveCollection(h => h.NodeIds);
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class ServerDatabaseConfiguration : IEntityTypeConfiguration<ServerDatabase>
{
    public void Configure(EntityTypeBuilder<ServerDatabase> builder)
    {
        builder.ToTable("server_databases");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Name).HasMaxLength(64);
        builder.Property(d => d.UserHost).HasMaxLength(DatabaseHost.MaxHostLength);
        // A database's name is unique on its host (it carries its server's short id).
        builder.HasIndex(d => new { d.HostId, d.Name }).IsUnique();
        builder.HasIndex(d => d.ServerId);
        // The databases are dropped before a server goes (ServerDatabases.DropAllAsync); a host with databases stays.
        builder.HasOne<Server>().WithMany().HasForeignKey(d => d.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DatabaseHost>().WithMany().HasForeignKey(d => d.HostId).OnDelete(DeleteBehavior.Restrict);
    }
}
