using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyHealthTag = "ready";

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

        services.AddHealthChecks().AddDbContextCheck<PulsarDbContext>("database", tags: [ReadyHealthTag]);

        return services;
    }
}
