using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Updates;

namespace Pulsar.Infrastructure.Updates;

/// <summary>
/// Looks for a newer release of Pulsar once a day (Phase 19), unless an administrator turned it off: a few minutes after
/// the panel starts, then a day after the last look. Each look is one pair of requests to GitHub.
/// </summary>
internal sealed partial class UpdateCheckService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<UpdateCheckService> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private static readonly TimeSpan s_firstLook = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_wake = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(s_firstLook, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
                    var state = await db.UpdateState.AsNoTracking().SingleOrDefaultAsync(stoppingToken);
                    if ((state?.CheckAutomatically ?? true) && (state?.CheckedAt is not { } at || clock.GetUtcNow() - at >= Every))
                    {
                        await scope.ServiceProvider.GetRequiredService<PanelUpdates>().CheckAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The database is down or restarting: the next wake tries again.
                    LogLookFailed(ex);
                }

                await Task.Delay(s_wake, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The daily look for Pulsar updates failed")]
    private partial void LogLookFailed(Exception exception);
}
