using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Scheduling;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// Moves the panel's game update checks and sequences of steps on in tests: passes as the panel's loops make them, the
/// waits of the steps and their polls cut short.
/// </summary>
internal static class SequenceDriver
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A pass of the game updates (as the panel's loop makes it): checks sent, automatic updates started.</summary>
    public static async Task GameUpdatesPassAsync(PulsarApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = new SystemRequestContext(GameUpdates.ActorName);
        await scope.ServiceProvider.GetRequiredService<GameUpdates>().RunDueAsync(DateTimeOffset.UtcNow, Ct);
    }

    /// <summary>
    /// A pass of the game updates and of the sequences, their waits and polls cut short. On the real clock: a pass on a
    /// later one would take sequences the panel's own runner holds (their lease is that clock's).
    /// </summary>
    public static async Task DriveAsync(PulsarApiFactory factory)
    {
        await GameUpdatesPassAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var now = DateTimeOffset.UtcNow;
            // A wait, or a step looking again at the server or its operation; not one a runner holds (its lease is minutes long).
            foreach (var waiting in (await db.ServerSequences.Where(q => q.Status == SequenceStatus.Running && q.StepBegun && q.WakeAt > now)
                         .ToListAsync(Ct)).Where(q => q.Current?.Kind == SequenceStepKind.Wait || q.WakeAt - now <= ServerSequences.PollInterval))
            {
                waiting.WaitUntil(now);
            }

            try
            {
                await db.SaveChangesAsync(Ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The runner moved it meanwhile.
            }
        }

        await factory.Services.GetRequiredService<ServerSequences>().RunDueAsync(DateTimeOffset.UtcNow, Ct);
    }

    /// <summary>The server's next check is due now (as after an update, or 15 minutes later): the next pass asks its node.</summary>
    public static async Task DueNowAsync(PulsarApiFactory factory, Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        for (var attempt = 0; ; attempt++)
        {
            if (await db.GameUpdateWatches.SingleOrDefaultAsync(w => w.ServerId == new ServerId(server), Ct) is not { } watch)
            {
                await GameUpdatesPassAsync(factory); // a pass makes the watches of servers that have none, due now
                continue;
            }

            watch.CheckSoon(DateTimeOffset.UtcNow);
            try
            {
                await db.SaveChangesAsync(Ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Has the node asked (a check due now, sent by the next pass) and waits until what it found matches.</summary>
    public static async Task<GameUpdateView> CheckedAsync(PulsarApiFactory factory, PanelClient admin, Guid server, Func<GameUpdateView, bool> found,
        bool ask = true)
    {
        if (ask)
        {
            await DueNowAsync(factory, server);
        }

        GameUpdateView? view = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            await GameUpdatesPassAsync(factory);
            view = await GameUpdatesAsync(admin, server);
            return view.CheckedAt is not null && !view.Checking && found(view);
        }, () => $"checked (last: {view?.Installed} {view?.Latest} {view?.Available} {view?.CheckErrorCode})");
        return view!;
    }

    public static async Task<GameUpdateView> GameUpdatesAsync(PanelClient client, Guid server) =>
        (await client.Http.GetFromJsonAsync<GameUpdateView>(new Uri($"/api/v1/servers/{server}/game-updates", UriKind.Relative), AccessScenario.Json,
            Ct))!;
}
