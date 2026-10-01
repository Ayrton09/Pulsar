using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>The panel database (PostgreSQL only). Schema conventions are described in docs/architecture/02.</summary>
public sealed class PulsarDbContext(DbContextOptions<PulsarDbContext> options)
    : IdentityUserContext<PulsarUser, UserId>(options), IPulsarDbContext
{
    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserRoleAssignment> UserRoles => Set<UserRoleAssignment>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();
        configurationBuilder.Properties<RoleId>().HaveConversion<RoleIdConverter>();
        configurationBuilder.Properties<NodeId>().HaveConversion<NodeIdConverter>();
        configurationBuilder.Properties<ServerId>().HaveConversion<ServerIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Identity tables keep Identity's shape under the project's names.
        builder.Entity<PulsarUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.Id).ValueGeneratedNever();
            user.Property(u => u.UserName).HasMaxLength(PulsarUser.MaxUserNameLength);
            user.Property(u => u.NormalizedUserName).HasMaxLength(PulsarUser.MaxUserNameLength);
            user.Property(u => u.DisplayName).HasMaxLength(PulsarUser.MaxDisplayNameLength);
            user.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
        });
        builder.Entity<IdentityUserClaim<UserId>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<UserId>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<UserId>>().ToTable("user_tokens");

        builder.ApplyConfigurationsFromAssembly(typeof(PulsarDbContext).Assembly);
    }
}
