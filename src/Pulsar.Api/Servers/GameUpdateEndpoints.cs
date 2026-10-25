using System.ComponentModel.DataAnnotations;
using Pulsar.Api.Http;
using Pulsar.Api.Realtime;
using Pulsar.Api.Security;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Servers;

/// <param name="WhenEmpty">An automatic update waits until nobody plays (games with a query only).</param>
/// <param name="WarningMinutes">How long before stopping the players are warned (0: no warning; games with a broadcast command).</param>
/// <param name="WarningMessage">What they are told, with {time} for how long is left; empty: the default.</param>
public sealed record GameUpdateSettingsRequest(
    GameUpdatePolicy Policy,
    bool WhenEmpty,
    [property: Range(0, GameUpdateWatch.MaxWarningMinutes)] int WarningMinutes,
    [property: StringLength(GameUpdateWatch.MaxWarningMessageLength)] string? WarningMessage);

/// <summary>
/// A server's game updates (Pulsar 1.0.9): what the last check found and how updates are followed (off, notified or
/// installed by themselves), a check now, an update installed now the way an automatic one is (warn, stop, update,
/// start again), and cancelling one. Installing by itself also needs the rights it uses, checked by the service.
/// </summary>
public static class GameUpdateEndpoints
{
    public static RouteGroupBuilder MapGameUpdateEndpoints(this RouteGroupBuilder api)
    {
        var updates = api.MapGroup("/servers/{id:guid}/game-updates").WithTags("Game updates");
        updates.MapGet("/", async (Guid id, GameUpdates u, CancellationToken ct) => (await u.GetAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View);
        updates.MapPut("/", async (Guid id, GameUpdateSettingsRequest request, GameUpdates u, CancellationToken ct) =>
                (await u.ChangeAsync(new ServerId(id), request.Policy, request.WhenEmpty, request.WarningMinutes, request.WarningMessage, ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);
        updates.MapPost("/check", async (Guid id, GameUpdates u, CancellationToken ct) => (await u.CheckNowAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);
        updates.MapPost("/install", async (Guid id, GameUpdates u, CancellationToken ct) => (await u.InstallNowAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);
        updates.MapPost("/cancel", async (Guid id, GameUpdates u, CancellationToken ct) => (await u.CancelAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.Install);
        return api;
    }
}

/// <summary>Players online as the live metrics last saw them (<see cref="LiveMetrics"/>), for the game updates.</summary>
internal sealed class LivePlayers(LiveMetrics metrics) : IServerPlayers
{
    public int? Online(ServerId server) => metrics.Find(server)?.Query?.Players;
}
