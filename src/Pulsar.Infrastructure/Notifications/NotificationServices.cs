using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Notifications;

namespace Pulsar.Infrastructure.Notifications;

/// <summary>
/// The notification sender loop: sends what is due, then sleeps until the next retry is due, deliveries are queued, or
/// a minute passes. Old deliveries are pruned once an hour.
/// </summary>
internal sealed partial class NotificationSenderService(NotificationDispatcher dispatcher, NotificationSignal signal, TimeProvider clock,
    ILogger<NotificationSenderService> logger) : BackgroundService
{
    private static readonly TimeSpan s_longestSleep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_pruneEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        var prunedAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                var more = await dispatcher.SendDueAsync(stoppingToken);
                if (clock.GetUtcNow() - prunedAt >= s_pruneEvery)
                {
                    await dispatcher.PruneAsync(stoppingToken);
                    prunedAt = clock.GetUtcNow();
                }

                var next = await dispatcher.NextDueAsync(stoppingToken);
                sleep = more ? TimeSpan.Zero
                    : next is { } at ? TimeSpan.FromTicks(Math.Clamp((at - clock.GetUtcNow()).Ticks, 0, s_longestSleep.Ticks))
                    : s_longestSleep;
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The database is down or restarting: try again a little later each time.
                failures++;
                sleep = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(failures - 1, 4))));
                LogPassFailed(ex, sleep);
            }

            try
            {
                await signal.WaitAsync(sleep, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The notification sender could not send due notifications; trying again in {Delay}")]
    private partial void LogPassFailed(Exception exception, TimeSpan delay);
}

/// <summary>Runs <see cref="NodeWatch"/>, then <see cref="ResourceWatch"/>, every <see cref="NodeWatch.Interval"/>.</summary>
internal sealed partial class NodeWatchService(NodeWatch watch, ResourceWatch resources, TimeProvider clock, ILogger<NodeWatchService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(NodeWatch.Interval, clock);
        do
        {
            try
            {
                await watch.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogSweepFailed(ex);
            }

            try
            {
                await resources.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogResourceSweepFailed(ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking the nodes for notifications failed; trying again at the next sweep")]
    private partial void LogSweepFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking servers and nodes for high CPU or memory failed; trying again at the next sweep")]
    private partial void LogResourceSweepFailed(Exception exception);
}
