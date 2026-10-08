using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Notifications;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Nodes;
using Pulsar.Infrastructure.Notifications;
using Pulsar.Infrastructure.Persistence;
using Pulsar.Infrastructure.Scheduling;
using Pulsar.Infrastructure.Servers;
using Pulsar.Infrastructure.Steam;

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
        services.AddSingleton<RealtimeChangesInterceptor>();
        services.AddDbContext<PulsarDbContext>((provider, options) => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(provider.GetRequiredService<RealtimeChangesInterceptor>()));
        services.AddScoped<IPulsarDbContext>(sp => sp.GetRequiredService<PulsarDbContext>());
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton(TimeProvider.System);

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready]);

        services.AddPulsarDataProtection(configuration);
        services.AddSingleton<IPanelKeyRing, PanelKeyRing>();
        services.AddSingleton<IVariableProtector, DataProtectionVariableProtector>();
        services.AddSingleton<IBackupStorageSecretProtector, DataProtectionBackupStorageSecretProtector>();
        services.AddScoped<IScheduledTaskClaims, ScheduledTaskClaims>();
        services.AddHostedService<SchedulerService>();

        services.AddSingleton<INotificationSecretProtector, DataProtectionNotificationSecretProtector>();
        services.AddScoped<INotificationClaims, NotificationClaims>();
        services.AddScoped<INotificationTransport, DiscordTransport>();
        services.AddScoped<INotificationTransport, EmailTransport>();
        // No redirects (the address was checked) and no request logging (the webhook URL holds its token).
        services.AddHttpClient(DiscordTransport.ClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHostedService<NotificationSenderService>();
        // Steam's public Workshop API: one host, no redirects; nothing secret in the requests.
        services.AddScoped<ISteamWorkshop, SteamWorkshopClient>();
        services.AddHttpClient(SteamWorkshopClient.ClientName, client =>
            {
                // Also the deadline for reading the body (SteamWorkshopClient reads it after the headers).
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHostedService<NodeWatchService>();

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
                options.Tokens.AuthenticatorTokenProvider = PulsarAuthenticatorTokenProvider.ProviderName;
            })
            .AddUserStore<PulsarUserStore>()
            .AddSignInManager<PulsarSignInManager>()
            .AddClaimsPrincipalFactory<PulsarClaimsPrincipalFactory>()
            // Password resets by administrators (Default) and authenticator apps; no email or SMS codes.
            .AddTokenProvider<DataProtectorTokenProvider<PulsarUser>>(TokenOptions.DefaultProvider)
            .AddTokenProvider<PulsarAuthenticatorTokenProvider>(PulsarAuthenticatorTokenProvider.ProviderName);

        return services;
    }
}
