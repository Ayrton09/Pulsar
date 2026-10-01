using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;

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
        return services;
    }
}
