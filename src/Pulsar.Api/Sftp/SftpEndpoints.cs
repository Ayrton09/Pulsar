using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Sftp;

/// <param name="PublicHost">The host name users connect to; null: the host they reach the panel by.</param>
/// <param name="HostKeyFingerprint">The host key's SHA-256 fingerprint as OpenSSH prints it (<c>SHA256:…</c>).</param>
/// <param name="Listening">The server listens now (false: turned off, or it could not start, see the panel log).</param>
public sealed record SftpSettingsResponse(bool Enabled, int Port, string? PublicHost, string HostKeyFingerprint, bool Listening);

/// <param name="PublicHost">A host name or IP address for the connection hint; leave it out for the panel's own host.</param>
public sealed record UpdateSftpSettingsRequest(bool Enabled, int Port, [property: StringLength(SftpSettings.MaxHostLength)] string? PublicHost);

/// <summary>How the signed-in user connects: <c>sftp://UserName@Host:Port</c>, checking the fingerprint at the first connection.</summary>
public sealed record SftpConnectionResponse(bool Enabled, string Host, int Port, string HostKeyFingerprint, string UserName);

/// <summary>
/// The SFTP server's settings (<c>settings.manage</c>, audited) and the connection hint for any signed-in user
/// (docs/guides/sftp.md).
/// </summary>
public static class SftpEndpoints
{
    public static RouteGroupBuilder MapSftpEndpoints(this RouteGroupBuilder api)
    {
        var sftp = api.MapGroup("/sftp").WithTags("SFTP");
        sftp.MapGet("/", GetConnectionAsync).RequireSignedInUser();
        sftp.MapGet("/settings", GetSettingsAsync).RequirePermission(Permissions.Settings.Manage);
        sftp.MapPut("/settings", UpdateSettingsAsync).RequirePermission(Permissions.Settings.Manage);
        return api;
    }

    private static async Task<Ok<SftpConnectionResponse>> GetConnectionAsync(HttpContext http, PulsarDbContext db, SftpHostKey key, CancellationToken ct)
    {
        var settings = await ReadAsync(db, ct);
        var loaded = await key.GetAsync(ct);
        var userName = http.User.Identity?.Name ?? string.Empty;

        // The hint only: the host a browser reached the panel by, unless the administrator named one.
        return TypedResults.Ok(new SftpConnectionResponse(settings.Enabled, settings.PublicHost ?? http.Request.Host.Host, settings.Port,
            loaded.Fingerprint, userName));
    }

    private static async Task<Ok<SftpSettingsResponse>> GetSettingsAsync(PulsarDbContext db, SftpHostKey key, SftpHost host, CancellationToken ct)
    {
        var settings = await ReadAsync(db, ct);
        var loaded = await key.GetAsync(ct);
        return TypedResults.Ok(new SftpSettingsResponse(settings.Enabled, settings.Port, settings.PublicHost, loaded.Fingerprint, host.ListeningPort is not null));
    }

    /// <summary>Turning SFTP on or off or changing its port restarts it: every SFTP connection ends.</summary>
    private static async Task<Results<Ok<SftpSettingsResponse>, ProblemHttpResult>> UpdateSettingsAsync(UpdateSftpSettingsRequest request,
        PulsarDbContext db, SftpHostKey key, SftpHost host, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var publicHost = string.IsNullOrWhiteSpace(request.PublicHost) ? null : request.PublicHost.Trim();
        if (SftpSettings.Validate(request.Port, publicHost) is { } error)
        {
            return ApiProblems.FromDomainError(error);
        }

        // The host key lives in the same row: made first, so this save never races its creation.
        await key.GetAsync(ct);
        var restart = false;
        for (var attempt = 0; ; attempt++)
        {
            var settings = await db.SftpSettings.SingleOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = SftpSettings.Default(clock.GetUtcNow());
                db.SftpSettings.Add(settings);
            }

            var (enabled, port) = (settings.Enabled, settings.Port);
            if (!settings.Change(request.Enabled, request.Port, publicHost, clock.GetUtcNow()))
            {
                db.ChangeTracker.Clear();
                break;
            }

            audit.Record(AuditActions.Sftp.SettingsChanged, metadata: new { request.Enabled, request.Port, publicHost = settings.PublicHost })
                .WithTarget("sftp", "settings");
            try
            {
                await db.SaveChangesAsync(ct);
                restart = enabled != settings.Enabled || port != settings.Port;
                break;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Changed meanwhile (another administrator): apply this change to what is stored now.
                db.ChangeTracker.Clear();
            }
        }

        if (restart)
        {
            host.Reload();
        }

        return await GetSettingsAsync(db, key, host, ct);
    }

    private static async Task<SftpSettings> ReadAsync(PulsarDbContext db, CancellationToken ct) =>
        await db.SftpSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? SftpSettings.Default(DateTimeOffset.UnixEpoch);
}
