using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Application.Realtime;
using Pulsar.Application.Servers;

namespace Pulsar.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPulsarApplication(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<PermissionCacheVersion>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<RoleManagement>();
        services.AddScoped<AuditQueries>();
        services.AddSingleton<AgentSessionStore>();
        services.AddSingleton<AgentChallengeStore>();
        services.AddScoped<AgentAccess>();
        services.AddScoped<NodeManagement>();
        services.AddScoped<PanelKeyManagement>();
        services.AddScoped<TemplateCatalog>();
        services.AddScoped<IBundledTemplates>(sp => sp.GetRequiredService<TemplateCatalog>());
        services.AddScoped<ServerManagement>();
        services.AddScoped<ConsoleCommands>();
        services.AddScoped<ServerFiles>();
        services.AddSingleton<FileTransfers>();
        services.TryAddSingleton(new FileManagerOptions());
        services.AddScoped<ServerSpecs>();
        services.AddScoped<NodeAllocations>();
        services.AddSingleton<OperationProgressCache>();
        services.AddScoped<AgentServerEvents>();
        services.TryAddSingleton<IRealtimeNotifier, NoRealtimeNotifier>();
        return services;
    }
}
