using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Certificates;

/// <param name="Sha256">The SHA-256 of the leaf's DER, lowercase hex.</param>
/// <param name="Staging">Issued by Let's Encrypt's staging environment: no browser trusts it.</param>
public sealed record PublicIpCertificateInfo(string Ip, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Sha256, bool Staging);

/// <param name="TermsOfServiceUrl">Let's Encrypt's subscriber agreement, which turning this on agrees to.</param>
/// <param name="ChallengePort">The port Let's Encrypt's http-01 check is answered on while this is on (80 unless the operator set another).</param>
/// <param name="Listening">The http-01 port is open now.</param>
/// <param name="ListenError">Why the http-01 port could not be opened, if it could not.</param>
/// <param name="Certificate">The certificate kept for the configured address, if any (expired ones included).</param>
/// <param name="Serving">That certificate is valid and served now to browsers that connect from the internet.</param>
/// <param name="Renewing">An attempt is running now.</param>
/// <param name="NextAttemptAt">When the next attempt starts: a renewal's due time, a failure's backoff or the hourly cap.</param>
/// <param name="AttemptsLeft">Attempts that may still start this hour (the button's included).</param>
public sealed record PublicIpCertificateResponse(
    bool Enabled,
    string? PublicIp,
    string? Email,
    AcmeDirectoryKind Directory,
    string TermsOfServiceUrl,
    int ChallengePort,
    bool Listening,
    string? ListenError,
    PublicIpCertificateInfo? Certificate,
    bool Serving,
    bool Renewing,
    DateTimeOffset? LastRenewalAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    int AttemptsLeft,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <param name="Enabled">Turning it on agrees to Let's Encrypt's subscriber agreement (<c>termsOfServiceUrl</c>).</param>
/// <param name="PublicIp">The panel's static public IPv4 or IPv6 address (required to turn it on).</param>
/// <param name="Email">Optional: where Let's Encrypt may write about the account.</param>
public sealed record UpdatePublicIpCertificateRequest(
    bool Enabled,
    [property: StringLength(PublicIpCertificateSettings.MaxIpLength)] string? PublicIp,
    [property: StringLength(PublicIpCertificateSettings.MaxEmailLength)] string? Email,
    AcmeDirectoryKind Directory = AcmeDirectoryKind.Production);

/// <summary>
/// The public IP certificate's settings and status (<c>settings.manage</c>, audited), and "Renew now"
/// (docs/guides/install.md, "Access from the internet with your public IP").
/// </summary>
public static class PublicIpCertificateEndpoints
{
    /// <summary>Where Let's Encrypt keeps its current subscriber agreement (what the UI links before anything is registered).</summary>
    public const string LetsEncryptRepository = "https://letsencrypt.org/repository/";

    public static RouteGroupBuilder MapPublicIpCertificateEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/public-ip-certificate").WithTags("Public IP certificate");
        group.MapGet("/", GetAsync).RequirePermission(Permissions.Settings.Manage);
        group.MapPut("/", UpdateAsync).RequirePermission(Permissions.Settings.Manage);
        group.MapPost("/renew", RenewAsync).RequirePermission(Permissions.Settings.Manage);
        return api;
    }

    private static async Task<Ok<PublicIpCertificateResponse>> GetAsync(IPulsarDbContext db, PublicIpCertificateService service,
        AcmeChallengeListener listener, PublicIpCertificateStore store, IOptions<PublicIpCertificateOptions> options, TimeProvider clock, CancellationToken ct) =>
        TypedResults.Ok(Describe(await ReadAsync(db, ct), service, listener, store, options.Value, clock.GetUtcNow()));

    /// <summary>
    /// Turning it on (agreeing to Let's Encrypt's terms), off, or to another address or directory. Another address or
    /// directory stops serving the certificate kept for the old one at once.
    /// </summary>
    private static async Task<Results<Ok<PublicIpCertificateResponse>, ProblemHttpResult>> UpdateAsync(UpdatePublicIpCertificateRequest request,
        IPulsarDbContext db, PublicIpCertificateService service, AcmeChallengeListener listener, PublicIpCertificateStore store,
        IOptions<PublicIpCertificateOptions> options, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var ip = string.IsNullOrWhiteSpace(request.PublicIp) ? null : request.PublicIp.Trim();
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (!Enum.IsDefined(request.Directory))
        {
            return ApiProblems.Create(StatusCodes.Status400BadRequest, "public_ip_certificate.invalid_directory", "The directory is production or staging.");
        }

        if (PublicIpCertificateSettings.Validate(request.Enabled, ip, email, out _) is { } error)
        {
            return ApiProblems.FromDomainError(error);
        }

        var changed = false;
        for (var attempt = 0; ; attempt++)
        {
            var settings = await db.PublicIpCertificateSettings.SingleOrDefaultAsync(ct);
            if (settings is null)
            {
                settings = PublicIpCertificateSettings.Default(clock.GetUtcNow());
                db.PublicIpCertificateSettings.Add(settings);
            }

            var wasEnabled = settings.Enabled;
            if (!settings.Change(request.Enabled, ip, email, request.Directory, clock.GetUtcNow()))
            {
                db.ChangeTracker.Clear();
                break;
            }

            audit.Record(AuditActions.PublicIpCertificate.SettingsChanged, metadata: new
            {
                request.Enabled,
                publicIp = settings.PublicIp,
                directory = settings.Directory == AcmeDirectoryKind.Staging ? "staging" : "production",
                emailSet = settings.ContactEmail is not null,
                termsAgreed = request.Enabled && !wasEnabled,
            })
                .WithTarget("public_ip_certificate", "settings");
            try
            {
                await db.SaveChangesAsync(ct);
                changed = true;
                break;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Changed meanwhile (another administrator, or a renewal): apply this change to what is stored now.
                db.ChangeTracker.Clear();
            }
        }

        var saved = await ReadAsync(db, ct);
        if (changed)
        {
            // What no longer matches stops being served now, not at the service's next look.
            if (store.Current is { } current && (!saved.Enabled || !saved.HasCurrentCertificate(clock.GetUtcNow()) || saved.CertificateSha256 != current.Sha256))
            {
                store.Serve(null);
            }

            service.Reload();
        }

        return TypedResults.Ok(Describe(saved, service, listener, store, options.Value, clock.GetUtcNow()));
    }

    /// <summary>
    /// Asks for a certificate now, without waiting for the renewal's time or a failure's backoff. Still at most
    /// <see cref="PublicIpCertificateSettings.MaxAttemptsPerHour"/> attempts an hour, so Let's Encrypt is never hammered.
    /// </summary>
    private static async Task<Results<Accepted<PublicIpCertificateResponse>, ProblemHttpResult>> RenewAsync(IPulsarDbContext db,
        PublicIpCertificateService service, AcmeChallengeListener listener, PublicIpCertificateStore store, IOptions<PublicIpCertificateOptions> options,
        IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var settings = await ReadAsync(db, ct);
        var now = clock.GetUtcNow();
        if (!settings.Enabled)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "public_ip_certificate.disabled", "Turn the public IP certificate on first.");
        }

        if (settings.AttemptsLeft(now) == 0)
        {
            var at = settings.AttemptsAllowedAt(now);
            return ApiProblems.Create(StatusCodes.Status409Conflict, "public_ip_certificate.too_many_attempts",
                "Let's Encrypt was asked enough times this hour.", $"Try again after {at.UtcDateTime:HH:mm} UTC.");
        }

        if (service.Renewing)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "public_ip_certificate.renewing", "A certificate is being asked for right now.");
        }

        audit.Record(AuditActions.PublicIpCertificate.RenewalRequested, metadata: new { publicIp = settings.PublicIp })
            .WithTarget("public_ip_certificate", "settings");
        await db.SaveChangesAsync(ct);
        service.RenewNow();
        return TypedResults.Accepted((string?)null, Describe(settings, service, listener, store, options.Value, now));
    }

    private static PublicIpCertificateResponse Describe(PublicIpCertificateSettings settings, PublicIpCertificateService service,
        AcmeChallengeListener listener, PublicIpCertificateStore store, PublicIpCertificateOptions options, DateTimeOffset now)
    {
        var certificate = settings.CertificateProtected is not null && settings.CertificateIp is { } ip && settings.CertificateNotBefore is { } from
            && settings.CertificateNotAfter is { } to && settings.CertificateSha256 is { } sha256
                ? new PublicIpCertificateInfo(ip, from, to, sha256, settings.CertificateDirectory == AcmeDirectoryKind.Staging)
                : null;
        var serving = settings.Enabled && store.Current is { } current && current.Sha256 == settings.CertificateSha256 && now < current.NotAfter;
        return new PublicIpCertificateResponse(
            settings.Enabled,
            settings.PublicIp,
            settings.ContactEmail,
            settings.Directory,
            settings.TermsOfServiceUrl ?? LetsEncryptRepository,
            options.ChallengePort,
            settings.Enabled && listener.ListeningPort is not null,
            settings.Enabled ? listener.Error : null,
            certificate,
            serving,
            service.Renewing,
            settings.IssuedAt,
            settings.LastAttemptAt,
            settings.Enabled && settings.PublicIp is not null ? settings.NextAttemptTime(now, forced: false) : null,
            settings.AttemptsLeft(now),
            settings.LastError,
            settings.LastErrorAt);
    }

    private static async Task<PublicIpCertificateSettings> ReadAsync(IPulsarDbContext db, CancellationToken ct) =>
        await db.PublicIpCertificateSettings.AsNoTracking().SingleOrDefaultAsync(ct) ?? PublicIpCertificateSettings.Default(DateTimeOffset.UnixEpoch);
}
