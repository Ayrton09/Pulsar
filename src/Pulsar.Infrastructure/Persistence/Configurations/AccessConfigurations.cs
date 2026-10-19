using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Access;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles", t => t.HasCheckConstraint("ck_roles_scope", SnakeCaseEnum<PermissionScope>.CheckConstraint("scope")));

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).HasMaxLength(Role.MaxNameLength);
        builder.Property(r => r.NormalizedName).HasMaxLength(Role.MaxNameLength);
        builder.HasIndex(r => r.NormalizedName).IsUnique();
        builder.Property(r => r.Description).HasMaxLength(Role.MaxDescriptionLength);
        builder.Property(r => r.Scope).HasConversion<SnakeCaseEnumConverter<PermissionScope>>();

        // UNIQUE (id, scope): lets assignment tables pin the scope of the role they reference with a composite FK.
        builder.HasAlternateKey(r => new { r.Id, r.Scope });

        // Optimistic concurrency on PostgreSQL's xmin system column.
        builder.Property<uint>("Version").IsRowVersion();

        builder.Ignore(r => r.Permissions);
        builder.OwnsMany(r => r.Grants, grant =>
        {
            grant.ToTable("role_permissions");
            grant.WithOwner().HasForeignKey(g => g.RoleId);
            grant.HasKey(g => new { g.RoleId, g.Permission });
            grant.Property(g => g.Permission).HasMaxLength(64);
        });
    }
}

internal sealed class UserRoleAssignmentConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.ToTable("user_roles", t => t.HasCheckConstraint("ck_user_roles_role_scope", "role_scope = 'global'"));

        builder.HasKey(a => new { a.UserId, a.RoleId });

        // Part of the composite FK, so a node or server role can never be assigned globally.
        builder.Property(a => a.RoleScope).HasConversion<SnakeCaseEnumConverter<PermissionScope>>();

        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany()
            .HasForeignKey(a => new { a.RoleId, a.RoleScope })
            .HasPrincipalKey(r => new { r.Id, r.Scope })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(a => a.GrantedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Node assignments (docs/architecture/02): gone with their user or node; their role cannot be deleted.</summary>
internal sealed class NodeAccessConfiguration : IEntityTypeConfiguration<NodeAccess>
{
    public void Configure(EntityTypeBuilder<NodeAccess> builder)
    {
        builder.ToTable("node_access", t => t.HasCheckConstraint("ck_node_access_role_scope", "role_scope = 'node'"));

        builder.HasKey(a => new { a.UserId, a.NodeId, a.RoleId });
        builder.HasIndex(a => a.NodeId);
        builder.Property(a => a.RoleScope).HasConversion<SnakeCaseEnumConverter<PermissionScope>>();

        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Node>().WithMany().HasForeignKey(a => a.NodeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany()
            .HasForeignKey(a => new { a.RoleId, a.RoleScope })
            .HasPrincipalKey(r => new { r.Id, r.Scope })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(a => a.GrantedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Server assignments (docs/architecture/02): gone with their user or server; their role cannot be deleted.</summary>
internal sealed class ServerAccessConfiguration : IEntityTypeConfiguration<ServerAccess>
{
    public void Configure(EntityTypeBuilder<ServerAccess> builder)
    {
        builder.ToTable("server_access", t => t.HasCheckConstraint("ck_server_access_role_scope", "role_scope = 'server'"));

        builder.HasKey(a => new { a.UserId, a.ServerId, a.RoleId });
        builder.HasIndex(a => a.ServerId);
        builder.Property(a => a.RoleScope).HasConversion<SnakeCaseEnumConverter<PermissionScope>>();

        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Server>().WithMany().HasForeignKey(a => a.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany()
            .HasForeignKey(a => new { a.RoleId, a.RoleScope })
            .HasPrincipalKey(r => new { r.Id, r.Scope })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(a => a.GrantedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>API keys (docs/architecture/02, "API keys"): found by the hash of their value; gone with their user.</summary>
internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.Name).HasMaxLength(ApiKey.MaxNameLength);
        builder.Property(k => k.Prefix).HasMaxLength(ApiKey.VisiblePrefixLength);
        builder.Property(k => k.TokenHash).HasMaxLength(32);
        builder.HasIndex(k => k.TokenHash).IsUnique();
        builder.HasIndex(k => k.UserId);
        builder.Property(k => k.Permissions).HasColumnType("text[]");
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SshKeyConfiguration : IEntityTypeConfiguration<SshKey>
{
    public void Configure(EntityTypeBuilder<SshKey> builder)
    {
        builder.ToTable("ssh_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.Name).HasMaxLength(SshKey.MaxNameLength);
        builder.Property(k => k.Type).HasMaxLength(SshPublicKey.MaxTypeLength);
        builder.Property(k => k.PublicKey).HasMaxLength(SshPublicKey.MaxBlobLength);
        builder.Property(k => k.Fingerprint).HasMaxLength(SshKey.FingerprintLength);

        // A key once per user (the sign-in finds it by its user and fingerprint); another user may add the same one.
        builder.HasIndex(k => new { k.UserId, k.Fingerprint }).IsUnique();
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
