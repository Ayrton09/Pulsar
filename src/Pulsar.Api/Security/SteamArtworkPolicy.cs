using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Appearance;

namespace Pulsar.Api.Security;

/// <summary>
/// Whether the Content-Security-Policy lets browsers load the game artwork from Steam's store CDN (the appearance setting,
/// T41), kept in memory for <see cref="SecurityHeaders"/> and changed only by the appearance endpoint; the panel is a
/// single instance. It is read from the database before the panel serves its first page (<see cref="SteamArtworkLoader"/>).
/// While it is unknown (not read yet, or a change whose outcome is unknown, until it is read again) it counts as off: no
/// browser is told it may contact Steam on a setting the panel has not seen.
/// </summary>
internal sealed class SteamArtworkPolicy
{
    private const int Unknown = 0;
    private const int Off = 1;
    private const int On = 2;

    private readonly Lock _lock = new();
    private int _state;
    private long _changes;

    public bool SteamArtwork => Volatile.Read(ref _state) == On;

    /// <summary>One change at a time, so the setting in memory is the one saved last.</summary>
    internal SemaphoreSlim Changing { get; } = new(1, 1);

    public async ValueTask EnsureLoadedAsync(IPulsarDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        long changes;
        lock (_lock)
        {
            if (_state != Unknown)
            {
                return;
            }

            changes = _changes;
        }

        var settings = await db.AppearanceSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        lock (_lock)
        {
            // A change made or forgotten meanwhile wins over what was read before it.
            if (_state == Unknown && _changes == changes)
            {
                Volatile.Write(ref _state, (settings?.SteamArtwork ?? AppearanceSettings.DefaultSteamArtwork) ? On : Off);
            }
        }
    }

    internal void Set(bool steamArtwork)
    {
        lock (_lock)
        {
            _changes++;
            Volatile.Write(ref _state, steamArtwork ? On : Off);
        }
    }

    /// <summary>A change whose outcome is unknown: off until it is read from the database again.</summary>
    internal void Forget()
    {
        lock (_lock)
        {
            _changes++;
            Volatile.Write(ref _state, Unknown);
        }
    }
}

/// <summary>
/// Reads the Steam artwork setting as the panel starts: hosted services start before Kestrel, so the first page already
/// has the right policy. A failure leaves it off and is logged; the next <c>GET /api/v1/appearance</c>, which the SPA
/// makes wherever it shows artwork, reads it again.
/// </summary>
internal sealed partial class SteamArtworkLoader(SteamArtworkPolicy policy, IServiceScopeFactory scopes, ILogger<SteamArtworkLoader> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await policy.EnsureLoadedAsync(scope.ServiceProvider.GetRequiredService<IPulsarDbContext>(), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            LogNotRead(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The appearance settings could not be read: the Steam artwork stays off until they can be")]
    private static partial void LogNotRead(ILogger logger, Exception exception);
}
