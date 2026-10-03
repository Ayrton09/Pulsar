using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Scheduling;

namespace Pulsar.Infrastructure.Scheduling;

/// <summary>
/// The panel's scheduler loop (ADR-0010): runs what is due, then sleeps until the next task is due, a task changes, or a
/// minute passes (a safety net for clock changes and other panels' edits). No polling beyond that.
/// </summary>
internal sealed partial class SchedulerService(Scheduler scheduler, ScheduleSignal signal, TimeProvider clock, ILogger<SchedulerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan s_longestSleep = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                var pass = await scheduler.RunDueAsync(clock.GetUtcNow(), stoppingToken);
                var next = await scheduler.NextRunAtAsync(stoppingToken);
                sleep = pass.More ? TimeSpan.Zero
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The scheduler could not run due tasks; trying again in {Delay}")]
    private partial void LogPassFailed(Exception exception, TimeSpan delay);
}
