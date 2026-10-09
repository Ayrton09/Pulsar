using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Metrics;

namespace Pulsar.Infrastructure.Metrics;

/// <summary>
/// Writes <see cref="MetricHistory"/>'s finished minutes every <see cref="Interval"/>, rolls the last hours up every few
/// minutes and prunes once an hour. Stopping writes the minute in progress too.
/// </summary>
internal sealed partial class MetricHistoryService(MetricHistory history, TimeProvider clock, ILogger<MetricHistoryService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan s_rollUpEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_pruneEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rolledUpAt = DateTimeOffset.MinValue;
        var prunedAt = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await history.FlushAsync(includeCurrentMinute: false, stoppingToken);
                    var now = clock.GetUtcNow();
                    if (now - rolledUpAt >= s_rollUpEvery)
                    {
                        await history.RollUpAsync(stoppingToken);
                        rolledUpAt = now;
                    }

                    if (now - prunedAt >= s_pruneEvery)
                    {
                        await history.PruneAsync(stoppingToken);
                        prunedAt = now;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The database is down or restarting: unwritten minutes are kept for a while and written next time.
                    LogPassFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await history.FlushAsync(includeCurrentMinute: true, cancellationToken);
        }
        catch (Exception ex)
        {
            LogFinalFlushFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the metric history failed; trying again shortly")]
    private partial void LogPassFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The metric history of the last minute could not be written while stopping")]
    private partial void LogFinalFlushFailed(Exception exception);
}
