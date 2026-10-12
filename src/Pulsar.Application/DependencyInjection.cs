using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Databases;
using Pulsar.Application.Metrics;
using Pulsar.Application.Nodes;
using Pulsar.Application.Notifications;
using Pulsar.Application.Realtime;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;

namespace Pulsar.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPulsarApplication(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<PermissionCacheVersion>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<VisibleResources>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<RoleManagement>();
        services.AddScoped<ApiKeys>();
        services.AddScoped<AuditQueries>();
        services.AddSingleton<AgentSessionStore>();
        services.AddSingleton<AgentTransportPolicy>();
        services.AddSingleton<AgentChallengeStore>();
        services.AddScoped<AgentAccess>();
        services.AddScoped<NodeManagement>();
        services.AddScoped<EnrollmentTokens>();
        services.AddScoped<PanelKeyManagement>();
        services.AddScoped<AgentTransportManagement>();
        services.AddScoped<TemplateCatalog>();
        services.AddScoped<IBundledTemplates>(sp => sp.GetRequiredService<TemplateCatalog>());
        services.AddScoped<ServerOperations>();
        services.AddScoped<ServerManagement>();
        services.AddScoped<ServerBackups>();
        services.AddScoped<ServerAddons>();
        services.AddScoped<WorkshopLookup>();
        services.AddScoped<BackupStorages>();
        services.AddScoped<SteamAccounts>();
        services.AddSingleton<SteamGuardCodes>();
        services.AddScoped<DatabaseHosts>();
        services.AddScoped<ServerDatabases>();
        services.AddScoped<ConsoleCommands>();
        services.AddScoped<ServerFiles>();
        services.AddSingleton<FileTransfers>();
        services.TryAddSingleton(new FileManagerOptions());
        services.AddScoped<ServerSpecs>();
        services.AddScoped<NodeAllocations>();
        services.AddSingleton<OperationProgressCache>();
        services.AddScoped<AgentServerEvents>();
        services.AddScoped<RequestContextOverride>();
        services.AddSingleton<ScheduleSignal>();
        services.AddSingleton<Scheduler>();
        services.AddScoped<ServerSchedules>();
        services.AddScoped<Notifier>();
        services.AddScoped<NotificationManagement>();
        services.AddSingleton<NotificationSignal>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<NodeWatch>();
        services.AddSingleton<MetricHistory>();
        services.TryAddSingleton<IRealtimeNotifier, NoRealtimeNotifier>();
        return services;
    }
}
