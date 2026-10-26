using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;

namespace Pulsar.Infrastructure.Scheduling;

/// <summary>
/// The loop of game updates and sequences (Pulsar 1.0.9): asks the nodes whose servers are due an update check, starts
/// the automatic updates that may start, and moves the running sequences on; then sleeps until the next one is due, a
/// sequence changes, or <see cref="s_longestSleep"/> passes (checks come due by themselves).
/// </summary>
internal sealed partial class SequenceService(IServiceScopeFactory scopes, ServerSequences sequences, SequenceSignal signal, TimeProvider clock,
    ILogger<SequenceService> logger) : BackgroundService
{
    private static readonly TimeSpan s_longestSleep = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                // The game updates' pass on its own: whatever stops it, the sequences already running go on.
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    // Before anything resolves the request context: what the pass starts is audited as the game updates.
                    scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = new SystemRequestContext(GameUpdates.ActorName);
                    await scope.ServiceProvider.GetRequiredService<GameUpdates>().RunDueAsync(clock.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogUpdatesPassFailed(ex);
                }

                var pass = await sequences.RunDueAsync(clock.GetUtcNow(), stoppingToken);
                var next = await sequences.NextWakeAtAsync(stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Game updates and sequences could not run; trying again in {Delay}")]
    private partial void LogPassFailed(Exception exception, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The game updates' pass failed; the sequences go on")]
    private partial void LogUpdatesPassFailed(Exception exception);
}
