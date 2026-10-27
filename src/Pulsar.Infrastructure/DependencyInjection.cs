using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Databases;
using Pulsar.Application.Metrics;
using Pulsar.Application.Nodes;
using Pulsar.Application.Notifications;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;
using Pulsar.Application.Updates;
using Pulsar.Contracts;
using Pulsar.Infrastructure.Databases;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Metrics;
using Pulsar.Infrastructure.Mods;
using Pulsar.Infrastructure.Nodes;
using Pulsar.Infrastructure.Notifications;
using Pulsar.Infrastructure.Persistence;
using Pulsar.Infrastructure.Scheduling;
using Pulsar.Infrastructure.Servers;
using Pulsar.Infrastructure.Steam;
using Pulsar.Infrastructure.Updates;

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
        services.AddSingleton<ISteamAccountSecretProtector, DataProtectionSteamAccountSecretProtector>();
        services.AddSingleton<IDatabaseSecretProtector, DataProtectionDatabaseSecretProtector>();
        services.AddSingleton<IDatabaseHostClient, MySqlDatabaseHostClient>();
        services.AddScoped<IRowLocks, RowLocks>();
        services.AddScoped<IScheduledTaskClaims, ScheduledTaskClaims>();
        services.AddScoped<IServerSequenceClaims, ServerSequenceClaims>();
        services.AddHostedService<SchedulerService>();
        services.AddHostedService<SequenceService>();

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
        // Mod catalogs (1.0.9). Modrinth's public API: one host, no redirects; it asks applications to name themselves.
        // CurseForge's: the administrator's own key, encrypted in the database; one host, no redirects.
        services.AddScoped<IModCatalog, ModrinthClient>();
        services.AddScoped<CurseForgeClient>();
        services.AddScoped<IModCatalog>(provider => provider.GetRequiredService<CurseForgeClient>());
        services.AddScoped<ICurseForgeKeyCheck>(provider => provider.GetRequiredService<CurseForgeClient>());
        services.AddScoped<ICurseForgeProjects>(provider => provider.GetRequiredService<CurseForgeClient>());
        services.AddSingleton<ICatalogKeyProtector, DataProtectionCatalogKeyProtector>();
        services.AddHttpClient(CurseForgeClient.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    $"Ayrton09/Pulsar/{PanelUpdates.PanelVersionText} (https://github.com/Ayrton09/Pulsar)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHttpClient(ModrinthClient.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    $"Ayrton09/Pulsar/{PanelUpdates.PanelVersionText} (https://github.com/Ayrton09/Pulsar)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        // uMod's plugin directory and Thunderstore's packages (1.0.14): one host each, no key, no redirects.
        services.AddScoped<IModCatalog, UmodClient>();
        services.AddScoped<IModCatalog, ThunderstoreClient>();
        services.AddHttpClient(ThunderstoreClient.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    $"Ayrton09/Pulsar/{PanelUpdates.PanelVersionText} (https://github.com/Ayrton09/Pulsar)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHttpClient(UmodClient.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    $"Ayrton09/Pulsar/{PanelUpdates.PanelVersionText} (https://github.com/Ayrton09/Pulsar)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHostedService<NodeWatchService>();
        // Pulsar's releases on GitHub (Phase 19): two small files a day, no redirects but to GitHub's release storage.
        services.AddScoped<IUpdateSource, GitHubUpdateSource>();
        services.AddHttpClient(GitHubUpdateSource.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"Pulsar-Panel/{PanelUpdates.PanelVersionText}");
            })
            .ConfigurePrimaryHttpMessageHandler(UpdateDownloads.CreateHandler)
            .RemoveAllLoggers();
        services.AddHostedService<UpdateCheckService>();
        services.AddOptions<MetricHistoryOptions>().Bind(configuration.GetSection(MetricHistoryOptions.SectionName))
            .Validate(o => o.MinuteRetention >= TimeSpan.FromDays(1) && o.HourRetention >= TimeSpan.FromDays(1),
                "MetricHistory: MinuteRetention and HourRetention must be at least a day (the 24-hour chart reads minutes).")
            .ValidateOnStart();
        services.AddScoped<IMetricHistoryStore, MetricHistoryStore>();
        services.AddHostedService<MetricHistoryService>();

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
