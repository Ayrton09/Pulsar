using System.Data.Common;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Security;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Realtime;
using Pulsar.Domain.Access;
using Pulsar.Domain.Appearance;
using Pulsar.Domain.Audit;

namespace Pulsar.Api.Appearance;

/// <param name="SteamArtwork">The built-in games show Steam's store artwork, which each visitor's browser loads from Steam's CDN.</param>
public sealed record AppearanceSettingsResponse(bool SteamArtwork);

public sealed record UpdateAppearanceSettingsRequest(bool SteamArtwork);

/// <summary>
/// How the panel looks for everyone: any signed-in user reads it (the pages follow it), <c>settings.manage</c> changes it
/// (audited). With the Steam artwork off, the Content-Security-Policy names no Steam origin either
/// (<see cref="SteamArtworkPolicy"/>, docs/architecture/08, T41).
/// </summary>
public static class AppearanceEndpoints
{
    public static RouteGroupBuilder MapAppearanceEndpoints(this RouteGroupBuilder api)
    {
        var appearance = api.MapGroup("/appearance").WithTags("Appearance");
        appearance.MapGet("/", GetAsync).RequireSignedInUser();
        appearance.MapPut("/", UpdateAsync).RequirePermission(Permissions.Settings.Manage);
        return api;
    }

    /// <summary>What is in effect: the policy the CSP follows, read from the database when it is not known yet.</summary>
    private static async Task<Ok<AppearanceSettingsResponse>> GetAsync(IPulsarDbContext db, SteamArtworkPolicy policy, CancellationToken ct)
    {
        await policy.EnsureLoadedAsync(db, ct);
        return TypedResults.Ok(new AppearanceSettingsResponse(policy.SteamArtwork));
    }

    /// <summary>
    /// The CSP of the next responses follows the change; a page already open keeps the policy it was served with until
    /// it is reloaded, but the live channel tells every browser to read the setting again, so open pages stop asking
    /// Steam for artwork at once.
    /// </summary>
    private static async Task<Ok<AppearanceSettingsResponse>> UpdateAsync(UpdateAppearanceSettingsRequest request, IPulsarDbContext db,
        SteamArtworkPolicy policy, IAuditLog audit, IRealtimeNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        // Decided under the lock, from what this change stored or put in effect; a spare notice only costs pages a read.
        var notify = false;
        await policy.Changing.WaitAsync(ct);
        try
        {
            var before = policy.SteamArtwork;
            try
            {
                notify = await SaveAsync(request, db, audit, clock, ct);
            }
            catch
            {
                // The change may have committed before the call failed (cancelled as the answer came back): the policy
                // follows what is stored, read again; when even that fails, it is off until it is read again. Pages are
                // told either way.
                notify = true;
                await ApplyStoredAsync(db, policy);
                throw;
            }

            policy.Set(request.SteamArtwork);
            notify |= policy.SteamArtwork != before;
        }
        finally
        {
            policy.Changing.Release();
            if (notify)
            {
                notifier.AppearanceChanged();
            }
        }

        return TypedResults.Ok(new AppearanceSettingsResponse(policy.SteamArtwork));
    }

    /// <summary>Whether the stored value changed.</summary>
    private static async Task<bool> SaveAsync(UpdateAppearanceSettingsRequest request, IPulsarDbContext db, IAuditLog audit, TimeProvider clock,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var now = clock.GetUtcNow();
            var settings = await db.AppearanceSettings.SingleOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = AppearanceSettings.Default(now);
                db.AppearanceSettings.Add(settings);
            }

            var previous = settings.SteamArtwork;
            if (!settings.ChangeSteamArtwork(request.SteamArtwork, now))
            {
                db.ChangeTracker.Clear();
                return false;
            }

            audit.Record(AuditActions.Appearance.SettingsChanged, metadata: new { steamArtwork = request.SteamArtwork, previousSteamArtwork = previous })
                .WithTarget("appearance", "settings");
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Created or changed meanwhile: apply this change to what is stored now.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task ApplyStoredAsync(IPulsarDbContext db, SteamArtworkPolicy policy)
    {
        // Off until what is stored is known, whatever this read meets.
        policy.Forget();
        try
        {
            db.ChangeTracker.Clear();
            await policy.EnsureLoadedAsync(db, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            // Still unknown: the next GET /api/v1/appearance reads it.
        }
    }
}
