using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Notifications;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Certificates;

/// <summary>
/// Keeps the public IP certificate (docs/guides/install.md, "Access from the internet with your public IP"): while the
/// feature is on, it answers http-01 on port 80 (<see cref="AcmeChallengeListener"/>), serves the stored certificate
/// (<see cref="PublicIpCertificateStore"/>), asks Let's Encrypt for one when there is none for the address, and renews it
/// at half its lifetime (or earlier when ARI says so). It never hammers Let's Encrypt: an attempt is counted in the
/// database before anything is sent, at most <see cref="PublicIpCertificateSettings.MaxAttemptsPerHour"/> start in an hour
/// (the button's included), and failures wait 15 minutes, doubling up to a day, or longer when the server asks for it.
/// Failures, a certificate close to expiry and a renewal after failures raise notifications.
/// </summary>
internal sealed partial class PublicIpCertificateService(
    IOptions<PublicIpCertificateOptions> options,
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    PublicIpCertificateStore store,
    AcmeChallenges challenges,
    AcmeChallengeListener listener,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<PublicIpCertificateService> logger) : BackgroundService
{
    private const string AccountKeyPurpose = "Pulsar.AcmeAccountKey.v1";
    private const string CertificatePurpose = "Pulsar.PublicIpCertificate.v1";

    /// <summary>At most this long between looks at the settings (a certificate's expiry, ARI, the listener).</summary>
    private static readonly TimeSpan s_idle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_listenRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_renewalInfoInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan s_failureNotificationInterval = TimeSpan.FromHours(12);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private volatile bool _forced;
    private volatile bool _renewing;

    /// <summary>An attempt is running now.</summary>
    public bool Renewing => _renewing;

    /// <summary>The settings changed: look at them again now.</summary>
    public void Reload() => _wake.Writer.TryWrite(true);

    /// <summary>The administrator's "Renew now": the next attempt starts at once (the hourly cap still holds).</summary>
    public void RenewNow()
    {
        _forced = true;
        Reload();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Run || !await StartedAsync(stoppingToken))
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    wait = await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ex is not OutOfMemoryException)
                {
                    LogFailed(logger, ex);
                    wait = s_listenRetry;
                }

                // Infinite (-1 ms) while the feature is off: until the settings change.
                if (wait != TimeSpan.Zero)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(wait);
                    try
                    {
                        await _wake.Reader.ReadAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Time to look again.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The panel stops.
        }
        finally
        {
            store.Serve(null);
            await listener.StopAsync();
        }
    }

    /// <summary>One look at the settings; how long until the next one (zero: at once).</summary>
    internal async Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken)
    {
        PublicIpCertificateSettings? settings;
        await using (var scope = scopes.CreateAsyncScope())
        {
            settings = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().PublicIpCertificateSettings.AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (settings is not { Enabled: true, PublicIp: not null })
        {
            store.Serve(null);
            await listener.StopAsync();
            return Timeout.InfiniteTimeSpan;
        }

        var now = clock.GetUtcNow();
        Serve(settings, now);
        if (!await listener.EnsureStartedAsync(cancellationToken))
        {
            // Nothing is asked of Let's Encrypt while its check could only fail: no attempt is spent.
            return s_listenRetry;
        }

        await NotifyExpiryAsync(settings, now, cancellationToken);
        await CheckRenewalInfoAsync(settings, now, cancellationToken);
        // A certificate kept but not served (the key ring cannot read it) is no certificate: a new one is asked for, with
        // the same backoff and hourly cap.
        var next = settings.NextAttemptTime(now, _forced, certificateUsable: store.Current?.Sha256 == settings.CertificateSha256);
        if (next > now)
        {
            var wait = next - now;
            return wait < s_idle ? wait : s_idle;
        }

        _forced = false;
        await AttemptAsync(cancellationToken);
        return TimeSpan.Zero;
    }

    /// <summary>Serves the stored certificate when it is for the configured address and valid; nothing otherwise.</summary>
    private void Serve(PublicIpCertificateSettings settings, DateTimeOffset now)
    {
        if (!settings.HasCurrentCertificate(now))
        {
            store.Serve(null);
            return;
        }

        if (store.Current?.Sha256 == settings.CertificateSha256)
        {
            return;
        }

        try
        {
            var (chainPem, key) = Unprotect(settings.CertificateProtected!);
            var chain = AcmeIssuance.ParseChain(chainPem);
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(key, out _);
                store.Serve(ServedPublicIpCertificate.Create(chain, ecdsa, IPAddress.Parse(settings.CertificateIp!)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                foreach (var certificate in chain)
                {
                    certificate.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or AcmeException or FormatException)
        {
            // The key ring is missing or another one: the certificate is renewed as if there were none.
            LogUnreadable(logger, ex.Message);
            store.Serve(null);
        }
    }

    private async Task AttemptAsync(CancellationToken cancellationToken)
    {
        _renewing = true;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var settings = await db.PublicIpCertificateSettings.SingleOrDefaultAsync(cancellationToken);
            if (settings is not { Enabled: true, PublicIp: { } ip })
            {
                return;
            }

            var now = clock.GetUtcNow();
            var directory = settings.Directory;
            settings.AttemptStarted(now);
            if (!await TrySaveAsync(db, cancellationToken))
            {
                return; // changed meanwhile: look again
            }

            LogAttempt(logger, ip, directory);
            string? error = null;
            TimeSpan? retryAfter = null;
            try
            {
                await IssueAsync(scope.ServiceProvider, db, settings, IPAddress.Parse(ip), directory, cancellationToken);
            }
            catch (AcmeException ex)
            {
                error = ex.Message;
                retryAfter = ex.Failure == AcmeFailure.RateLimited ? ex.RetryAfter : null;
            }
            catch (CryptographicException ex)
            {
                error = "The ACME account key cannot be read: the Data Protection key ring is missing or another one. " + ex.Message;
            }

            if (error is not null)
            {
                LogAttemptFailed(logger, ip, error);
                await RecordFailureAsync(scope.ServiceProvider, ip, directory, error, retryAfter, cancellationToken);
            }
        }
        finally
        {
            _renewing = false;
        }
    }

    private async Task IssueAsync(IServiceProvider services, IPulsarDbContext db, PublicIpCertificateSettings settings, IPAddress address,
        AcmeDirectoryKind directory, CancellationToken cancellationToken)
    {
        var settingsOptions = options.Value;
        using var accountKey = await AccountKeyAsync(db, settings, cancellationToken);
        using var client = new AcmeClient(settingsOptions.HandlerOverride?.Invoke() ?? AcmeClient.CreateHandler(),
            new Uri(settingsOptions.DirectoryUrl(directory)), accountKey);
        var offered = await client.GetDirectoryAsync(cancellationToken);
        if (!offered.Profiles.Contains(AcmeClient.ShortLivedProfile))
        {
            throw new AcmeException("This ACME server does not offer the shortlived profile, the only one for IP address certificates.");
        }

        var contact = settings.ContactEmail ?? string.Empty;
        if (settings.HasAccountAt(directory))
        {
            client.UseAccount(settings.AccountUrl!);
            if (settings.AccountContact != contact)
            {
                await client.UpdateContactAsync(settings.ContactEmail, cancellationToken);
                settings.AccountRegistered(settings.AccountUrl!, directory, contact, null);
                await TrySaveAsync(db, cancellationToken);
            }
        }
        else
        {
            var url = await client.RegisterAccountAsync(settings.ContactEmail, cancellationToken);
            settings.AccountRegistered(url, directory, contact, offered.TermsOfService);
            if (!await TrySaveAsync(db, cancellationToken))
            {
                throw new AcmeException("The settings changed while the account was registered: trying again.");
            }

            LogAccount(logger, directory);
        }

        var now = clock.GetUtcNow();
        var replaces = settings.HasCurrentCertificate(now) && offered.RenewalInfo is not null ? settings.CertificateAriId : null;
        using var issued = await AcmeIssuance.IssueAsync(client, address, replaces, challenges, settingsOptions.ValidationTimeout,
            settingsOptions.PollInterval, clock, cancellationToken);
        var leaf = issued.Leaf;
        var notBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(leaf.RawData));
        var protector = dataProtection.CreateProtector(CertificatePurpose);
        var stored = protector.Protect(Pack(issued.ChainPem, issued.PrivateKey));
        var hadFailures = settings.FailureNotifiedAt is not null;
        settings.Issued(stored, address.ToString(), directory, notBefore, notAfter, sha256, AcmeIssuance.RenewalInfoId(leaf), clock.GetUtcNow());
        if (hadFailures)
        {
            settings.FailureNotificationCleared();
        }

        // Served first: the certificate is good whether or not this save races an administrator's.
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportPkcs8PrivateKey(issued.PrivateKey, out _);
            store.Serve(ServedPublicIpCertificate.Create(issued.Chain, ecdsa, address));
        }

        var ip = address.ToString();
        await SaveIssuedAsync(services, db, settings, ip, directory, hadFailures, cancellationToken);
        LogIssued(logger, ip, notAfter);
    }

    /// <summary>
    /// Saves the new certificate; when an administrator saved the row meanwhile, applies it to the row as it is now, unless
    /// the address or directory changed (the certificate is then for nothing configured).
    /// </summary>
    private async Task SaveIssuedAsync(IServiceProvider services, IPulsarDbContext db, PublicIpCertificateSettings issued, string ip,
        AcmeDirectoryKind directory, bool notifyRecovered, CancellationToken cancellationToken)
    {
        var notifier = services.GetRequiredService<Notifier>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (notifyRecovered)
            {
                await notifier.PublicIpCertificateRenewedAsync(ip, issued.CertificateNotAfter!.Value, cancellationToken);
            }

            if (await TrySaveAsync(db, cancellationToken))
            {
                return;
            }

            db.ChangeTracker.Clear();
            var current = await db.PublicIpCertificateSettings.SingleOrDefaultAsync(cancellationToken);
            if (current is null || current.PublicIp != ip || current.Directory != directory)
            {
                store.Serve(null);
                return;
            }

            current.Issued(issued.CertificateProtected!, ip, directory, issued.CertificateNotBefore!.Value, issued.CertificateNotAfter!.Value,
                issued.CertificateSha256!, issued.CertificateAriId, clock.GetUtcNow());
            if (notifyRecovered)
            {
                current.FailureNotificationCleared();
            }
        }
    }

    private async Task RecordFailureAsync(IServiceProvider services, string ip, AcmeDirectoryKind directory, string error, TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<IPulsarDbContext>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            db.ChangeTracker.Clear();
            var settings = await db.PublicIpCertificateSettings.SingleOrDefaultAsync(cancellationToken);
            if (settings is null || settings.PublicIp != ip || settings.Directory != directory)
            {
                return; // another address now: its own attempts start afresh
            }

            var now = clock.GetUtcNow();
            settings.Failed(error, retryAfter, now);
            if (settings.FailureNotifiedAt is not { } told || now - told >= s_failureNotificationInterval)
            {
                await services.GetRequiredService<Notifier>().PublicIpCertificateFailedAsync(ip, error,
                    settings.HasCurrentCertificate(now) ? settings.CertificateNotAfter : null, cancellationToken);
                settings.FailureNotified(now);
            }

            if (await TrySaveAsync(db, cancellationToken))
            {
                return;
            }
        }
    }

    /// <summary>Once per certificate: it expires within 48 hours (or 30 % of its lifetime) and no new one came.</summary>
    private async Task NotifyExpiryAsync(PublicIpCertificateSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!settings.HasCurrentCertificate(now) || settings.ExpiryNotifiedFor == settings.CertificateSha256)
        {
            return;
        }

        var lifetime = settings.CertificateNotAfter!.Value - settings.CertificateNotBefore!.Value;
        var warning = TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(48).Ticks, lifetime.Ticks * 3 / 10));
        if (settings.CertificateNotAfter.Value - now > warning)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var tracked = await db.PublicIpCertificateSettings.SingleOrDefaultAsync(cancellationToken);
        if (tracked is null || tracked.CertificateSha256 != settings.CertificateSha256 || tracked.ExpiryNotifiedFor == settings.CertificateSha256)
        {
            return;
        }

        tracked.ExpiryNotified(settings.CertificateSha256!);
        await scope.ServiceProvider.GetRequiredService<Notifier>().PublicIpCertificateExpiringAsync(settings.CertificateIp!, settings.CertificateSha256!,
            settings.CertificateNotAfter.Value, cancellationToken);
        await TrySaveAsync(db, cancellationToken);
    }

    /// <summary>
    /// ARI (RFC 9773), every 6 hours while a certificate is served: an earlier suggested window (a revocation coming) moves
    /// the renewal to a random moment in it. A failure here changes nothing (the half-life still holds).
    /// </summary>
    private async Task CheckRenewalInfoAsync(PublicIpCertificateSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!settings.HasCurrentCertificate(now) || settings.CertificateAriId is not { } id
            || (settings.RenewalInfoCheckedAt is { } checkedAt && now - checkedAt < s_renewalInfoInterval)
            || settings.AccountKeyProtected is null)
        {
            return;
        }

        DateTimeOffset? suggested = null;
        try
        {
            var settingsOptions = options.Value;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var client = new AcmeClient(settingsOptions.HandlerOverride?.Invoke() ?? AcmeClient.CreateHandler(),
                new Uri(settingsOptions.DirectoryUrl(settings.Directory)), key);
            if (await client.GetRenewalInfoAsync(id, cancellationToken) is { } window)
            {
                var span = window.End - window.Start;
                suggested = window.Start + TimeSpan.FromTicks((long)(span.Ticks * Random.Shared.NextDouble()));
            }
        }
        catch (AcmeException ex)
        {
            LogRenewalInfoFailed(logger, ex.Message);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        var tracked = await db.PublicIpCertificateSettings.SingleOrDefaultAsync(cancellationToken);
        if (tracked is not null && tracked.CertificateSha256 == settings.CertificateSha256)
        {
            tracked.RenewalInfoChecked(suggested, now);
            await TrySaveAsync(db, cancellationToken);
        }
    }

    /// <summary>The account key: made at the first attempt, kept encrypted with Data Protection, never replaced.</summary>
    private async Task<ECDsa> AccountKeyAsync(IPulsarDbContext db, PublicIpCertificateSettings settings, CancellationToken cancellationToken)
    {
        var protector = dataProtection.CreateProtector(AccountKeyPurpose);
        var key = ECDsa.Create();
        try
        {
            if (settings.AccountKeyProtected is { } stored)
            {
                var pkcs8 = protector.Unprotect(stored);
                try
                {
                    key.ImportPkcs8PrivateKey(pkcs8, out _);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }

                return key;
            }

            key.GenerateKey(ECCurve.NamedCurves.nistP256);
            var created = key.ExportPkcs8PrivateKey();
            try
            {
                settings.SetAccountKey(protector.Protect(created));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(created);
            }

            if (!await TrySaveAsync(db, cancellationToken))
            {
                throw new AcmeException("The settings changed while the account key was made: trying again.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static async Task<bool> TrySaveAsync(IPulsarDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>The chain's PEM and the key's PKCS#8 in one blob: a length, then each.</summary>
    private static byte[] Pack(string chainPem, byte[] key)
    {
        var chain = Encoding.ASCII.GetBytes(chainPem);
        var packed = new byte[4 + chain.Length + key.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packed, chain.Length);
        chain.CopyTo(packed, 4);
        key.CopyTo(packed, 4 + chain.Length);
        return packed;
    }

    private (string ChainPem, byte[] Key) Unprotect(byte[] stored)
    {
        var packed = dataProtection.CreateProtector(CertificatePurpose).Unprotect(stored);
        try
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packed);
            if (length < 0 || length > packed.Length - 4)
            {
                throw new FormatException("The stored public IP certificate is damaged.");
            }

            return (Encoding.ASCII.GetString(packed, 4, length), packed[(4 + length)..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(packed);
        }
    }

    private async Task<bool> StartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Asking Let's Encrypt ({Directory}) for a certificate for {Address}")]
    private static partial void LogAttempt(ILogger logger, string address, AcmeDirectoryKind directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered the ACME account at Let's Encrypt ({Directory})")]
    private static partial void LogAccount(ILogger logger, AcmeDirectoryKind directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Let's Encrypt issued the certificate for {Address}, valid until {NotAfter}")]
    private static partial void LogIssued(ILogger logger, string address, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The certificate for {Address} could not be obtained: {Reason}")]
    private static partial void LogAttemptFailed(ILogger logger, string address, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored public IP certificate cannot be read ({Reason}): it is renewed as if there were none")]
    private static partial void LogUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Let's Encrypt's renewal information could not be read: {Reason}")]
    private static partial void LogRenewalInfoFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The public IP certificate's renewal failed unexpectedly")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
