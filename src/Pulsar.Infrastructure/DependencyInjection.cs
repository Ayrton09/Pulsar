using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Nodes;
using Pulsar.Infrastructure.Persistence;
using Pulsar.Infrastructure.Servers;

namespace Pulsar.Infrastructure;

/// <summary>Health check tags: <see cref="Ready"/> checks gate <c>/health/ready</c>.</summary>
public static class HealthTags
{
    public const string Ready = "ready";
}

public static class DependencyInjection
{
    public static IServiceCollection AddPulsarInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new DatabaseStartupException(
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} is not configured. Pulsar requires PostgreSQL " +
                $"{DatabaseOptions.MinimumPostgresMajorVersion} or newer (for example: " +
                "Host=localhost;Port=5432;Database=pulsar;Username=pulsar;Password=...).");
        }

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddDbContext<PulsarDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IPulsarDbContext>(sp => sp.GetRequiredService<PulsarDbContext>());
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton(TimeProvider.System);

        services.AddHealthChecks().AddDbContextCheck<PulsarDbContext>("database", tags: [HealthTags.Ready]);

        services.AddPulsarDataProtection(configuration);
        services.AddSingleton<IPanelKeyRing, PanelKeyRing>();
        services.AddSingleton<IVariableProtector, DataProtectionVariableProtector>();

        services.AddIdentityCore<PulsarUser>(options =>
            {
                // Length over composition rules (NIST SP 800-63B); Identity hashes with PBKDF2.
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<PulsarDbContext>()
            .AddSignInManager<PulsarSignInManager>()
            .AddDefaultTokenProviders();

        return services;
    }
}
