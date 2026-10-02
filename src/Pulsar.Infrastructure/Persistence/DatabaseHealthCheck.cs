using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>
/// Readiness of the database. Unlike EF Core's <c>AddDbContextCheck</c>, which reports any exception as unhealthy,
/// a caller that goes away mid-check (a probe's timeout) cancels the check instead of logging a database error:
/// the health check service lets that cancellation through, and the request ends as aborted (499).
/// Other failures still become the registration's failure status.
/// </summary>
internal sealed class DatabaseHealthCheck(PulsarDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "Cannot connect to the database.");
}
