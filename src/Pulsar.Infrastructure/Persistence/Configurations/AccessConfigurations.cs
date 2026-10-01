using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pulsar.Domain.Access;
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
