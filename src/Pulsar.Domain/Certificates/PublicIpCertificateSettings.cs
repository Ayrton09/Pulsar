using Pulsar.Domain.Common;
using Pulsar.Domain.Notifications;

namespace Pulsar.Domain.Certificates;

/// <summary>Which Let's Encrypt directory the panel asks: production (trusted certificates) or staging (tests only).</summary>
public enum AcmeDirectoryKind
{
    Production,

    /// <summary>Let's Encrypt's staging environment: same flow, generous limits, certificates no browser trusts.</summary>
    Staging,
}

/// <summary>
/// The panel's public IP certificate (one row, docs/guides/install.md "Access from the internet with your public IP"): a
/// Let's Encrypt certificate for the panel's static public IP address, served to browsers that connect to it from the
/// internet. Off by default. The ACME account key and the certificate's private key are kept encrypted with ASP.NET Core
/// Data Protection (ADR-0011), as the panel signing key and the SFTP host key are; the account key is made once and kept.
/// What the renewal needs to never hammer Let's Encrypt is kept here too, so a restart does not forget it: the attempts of
/// the current hour, the consecutive failures and when the next attempt may start.
/// </summary>
public sealed class PublicIpCertificateSettings
{
    public const int SingletonId = 1;
    public const int MaxIpLength = 45;
    public const int MaxEmailLength = NotificationChannel.MaxEmailLength;
    public const int MaxErrorLength = 512;
    public const int MaxUrlLength = 512;

    /// <summary>Attempts that may start within one hour, the button's included (Let's Encrypt allows 5 failures an hour).</summary>
    public const int MaxAttemptsPerHour = 3;

    /// <summary>The first wait after a failed attempt; each further failure doubles it, up to <see cref="MaxBackoff"/>.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest wait between failed attempts: one a day never adds up to a pause of the address at Let's Encrypt
    /// (its consecutive-failure limit refills one a day).
    /// </summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);

    /// <summary>The longest a server's Retry-After is honoured (a longer one is taken as this).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromDays(7);

    private PublicIpCertificateSettings()
    {
        // For EF Core materialization.
    }

    public int Id { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>The public IP address the certificate is for, as <see cref="System.Net.IPAddress.ToString"/> writes it.</summary>
    public string? PublicIp { get; private set; }

    /// <summary>Where Let's Encrypt may write about the account (optional; it sends no expiry mails any more).</summary>
    public string? ContactEmail { get; private set; }

    public AcmeDirectoryKind Directory { get; private set; }

    /// <summary>When an administrator turned it on, agreeing to Let's Encrypt's subscriber agreement.</summary>
    public DateTimeOffset? TermsAgreedAt { get; private set; }

    /// <summary>The ACME account key (ECDSA P-256, PKCS#8) encrypted with Data Protection; null until the first attempt.</summary>
    public byte[]? AccountKeyProtected { get; private set; }

    /// <summary>The account's URL at <see cref="AccountDirectory"/> (its key id in signed requests).</summary>
    public string? AccountUrl { get; private set; }

    public AcmeDirectoryKind? AccountDirectory { get; private set; }

    /// <summary>The contact the account was registered or last updated with ("" for none).</summary>
    public string? AccountContact { get; private set; }

    /// <summary>The subscriber agreement's address the directory named when the account was registered.</summary>
    public string? TermsOfServiceUrl { get; private set; }

    /// <summary>The certificate chain (PEM) and its private key (PKCS#8), encrypted with Data Protection.</summary>
    public byte[]? CertificateProtected { get; private set; }

    public string? CertificateIp { get; private set; }

    public AcmeDirectoryKind? CertificateDirectory { get; private set; }

    public DateTimeOffset? CertificateNotBefore { get; private set; }

    public DateTimeOffset? CertificateNotAfter { get; private set; }

    /// <summary>ARI's identifier of the certificate (RFC 9773: authority key identifier and serial number).</summary>
    public string? CertificateAriId { get; private set; }

    /// <summary>The SHA-256 of the leaf's DER, lowercase hex.</summary>
    public string? CertificateSha256 { get; private set; }

    /// <summary>When the current certificate was issued (the last renewal).</summary>
    public DateTimeOffset? IssuedAt { get; private set; }

    /// <summary>When the current certificate is due for renewal: half its lifetime, or earlier when ARI says so.</summary>
    public DateTimeOffset? RenewAfter { get; private set; }

    /// <summary>When ARI was last asked about the current certificate.</summary>
    public DateTimeOffset? RenewalInfoCheckedAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>After a failure: the next attempt starts no earlier (the button's included is the hourly cap only).</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastErrorAt { get; private set; }

    /// <summary>When the failure notification was last raised (at most one every 12 hours).</summary>
    public DateTimeOffset? FailureNotifiedAt { get; private set; }

    /// <summary>The certificate whose coming expiry was notified (its SHA-256), so it is told once.</summary>
    public string? ExpiryNotifiedFor { get; private set; }

    /// <summary>When the attempts of the last hour started (a rolling hour: at most <see cref="MaxAttemptsPerHour"/>).</summary>
    public IReadOnlyList<DateTimeOffset> RecentAttempts { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PublicIpCertificateSettings Default(DateTimeOffset now) => new() { Id = SingletonId, Directory = AcmeDirectoryKind.Production, UpdatedAt = now };

    /// <summary>The address as stored, or the reason it cannot be (null when it can).</summary>
    public static DomainError? Validate(bool enabled, string? publicIp, string? email, out string? normalizedIp)
    {
        normalizedIp = null;
        if (publicIp is not null)
        {
            if (!PublicAddresses.TryParse(publicIp, out var address))
            {
                return PublicIpCertificateErrors.IpInvalid;
            }

            if (!PublicAddresses.IsGloballyRoutable(address))
            {
                return PublicIpCertificateErrors.IpNotPublic;
            }

            normalizedIp = address.ToString();
        }
        else if (enabled)
        {
            return PublicIpCertificateErrors.IpRequired;
        }

        return email is not null && !NotificationChannel.IsEmailAddress(email) ? PublicIpCertificateErrors.EmailInvalid : null;
    }

    /// <summary>
    /// False when nothing changed. Turning it on records that the administrator agreed to Let's Encrypt's terms; another
    /// address or directory starts afresh (no backoff carried over from the old one: the hourly cap still holds). Turning
    /// it off, or another address or directory, forgets the certificate and its key: nothing keeps key material that
    /// nothing serves.
    /// </summary>
    public bool Change(bool enabled, string? publicIp, string? email, AcmeDirectoryKind directory, DateTimeOffset now)
    {
        if (Validate(enabled, publicIp, email, out var ip) is { } error)
        {
            throw new ArgumentException(error.Message, nameof(publicIp));
        }

        if (Enabled == enabled && PublicIp == ip && ContactEmail == email && Directory == directory)
        {
            return false;
        }

        if (enabled && !Enabled)
        {
            TermsAgreedAt = now;
        }

        if (PublicIp != ip || Directory != directory)
        {
            NextAttemptAt = null;
            ConsecutiveFailures = 0;
            LastError = null;
            LastErrorAt = null;
            FailureNotifiedAt = null;
        }

        if (!enabled || PublicIp != ip || Directory != directory)
        {
            ForgetCertificate();
        }

        Enabled = enabled;
        PublicIp = ip;
        ContactEmail = email;
        Directory = directory;
        UpdatedAt = now;
        return true;
    }

    /// <summary>The account key made at the first attempt; never replaced.</summary>
    public void SetAccountKey(byte[] protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        if (AccountKeyProtected is not null)
        {
            throw new InvalidOperationException("The ACME account key already exists.");
        }

        AccountKeyProtected = protectedKey;
    }

    /// <summary>
    /// A new account key in place of one nothing can read any more (the Data Protection key ring was lost): the old account
    /// is forgotten, so a new one is registered with this key at the next attempt.
    /// </summary>
    public void ReplaceAccountKey(byte[] protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        AccountKeyProtected = protectedKey;
        AccountUrl = null;
        AccountDirectory = null;
        AccountContact = null;
    }

    public void AccountRegistered(string url, AcmeDirectoryKind directory, string contact, string? termsOfService)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        AccountUrl = url;
        AccountDirectory = directory;
        AccountContact = contact;
        TermsOfServiceUrl = termsOfService ?? TermsOfServiceUrl;
    }

    /// <summary>The account is the one to sign with for <paramref name="directory"/>.</summary>
    public bool HasAccountAt(AcmeDirectoryKind directory) => AccountUrl is not null && AccountDirectory == directory;

    /// <summary>A certificate for the configured address and directory that is valid at <paramref name="now"/>.</summary>
    public bool HasCurrentCertificate(DateTimeOffset now) =>
        CertificateProtected is not null && CertificateIp == PublicIp && CertificateDirectory == Directory
        && CertificateNotBefore <= now && now < CertificateNotAfter;

    /// <summary>How many attempts may still start now: those of the last hour count (a rolling hour).</summary>
    public int AttemptsLeft(DateTimeOffset now) => Math.Max(0, MaxAttemptsPerHour - WithinTheHour(now).Count());

    /// <summary>When the next attempt may start under the hourly cap: an hour after the oldest attempt that fills it.</summary>
    public DateTimeOffset AttemptsAllowedAt(DateTimeOffset now)
    {
        var recent = WithinTheHour(now).Order().ToList();
        return recent.Count < MaxAttemptsPerHour ? now : recent[recent.Count - MaxAttemptsPerHour] + TimeSpan.FromHours(1);
    }

    /// <summary>
    /// When the renewal should next try (it may be now or past): at once when there is no certificate for this address and
    /// directory (or the one kept cannot be used: <paramref name="certificateUsable"/>, a key ring that cannot read it),
    /// otherwise when it is due; never before a failure's backoff unless <paramref name="forced"/> (the button), and never
    /// more often than the hourly cap.
    /// </summary>
    public DateTimeOffset NextAttemptTime(DateTimeOffset now, bool forced, bool certificateUsable = true)
    {
        var due = certificateUsable && HasCurrentCertificate(now) ? RenewAfter ?? HalfLife() : now;
        if (forced)
        {
            due = now;
        }
        else if (NextAttemptAt is { } backoff && backoff > due)
        {
            due = backoff;
        }

        var allowed = AttemptsAllowedAt(now);
        return allowed > due ? allowed : due;
    }

    /// <summary>Counts an attempt before anything is sent, so a crash or restart in the middle still counts it.</summary>
    public void AttemptStarted(DateTimeOffset now)
    {
        RecentAttempts = [.. WithinTheHour(now), now];
        LastAttemptAt = now;
        UpdatedAt = now;
    }

    /// <summary>The wait after the <paramref name="failures"/>th failure in a row: 15 minutes, doubling up to a day.</summary>
    public static TimeSpan Backoff(int failures)
    {
        var doublings = Math.Clamp(failures - 1, 0, 16);
        var wait = TimeSpan.FromTicks(FirstBackoff.Ticks << doublings);
        return wait > MaxBackoff ? MaxBackoff : wait;
    }

    /// <summary>A failed attempt: waits the backoff, or what the server asked for when that is longer (up to a week).</summary>
    public void Failed(string error, TimeSpan? retryAfter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        ConsecutiveFailures++;
        var wait = Backoff(ConsecutiveFailures);
        if (retryAfter is { } asked && asked > wait)
        {
            wait = asked > MaxRetryAfter ? MaxRetryAfter : asked;
        }

        NextAttemptAt = now + wait;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        LastErrorAt = now;
        UpdatedAt = now;
    }

    /// <summary>A new certificate: the failures and their backoff are over.</summary>
    public void Issued(byte[] protectedCertificate, string ip, AcmeDirectoryKind directory, DateTimeOffset notBefore, DateTimeOffset notAfter,
        string sha256, string? ariId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(protectedCertificate);
        CertificateProtected = protectedCertificate;
        CertificateIp = ip;
        CertificateDirectory = directory;
        CertificateNotBefore = notBefore;
        CertificateNotAfter = notAfter;
        CertificateSha256 = sha256;
        CertificateAriId = ariId;
        IssuedAt = now;
        RenewAfter = HalfLife();
        RenewalInfoCheckedAt = null;
        NextAttemptAt = null;
        ConsecutiveFailures = 0;
        LastError = null;
        LastErrorAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// ARI's suggested renewal time, when it comes before the half-life (a revocation ahead, say) but not before a third of
    /// the lifetime after the issue: a window in the past (a server's mistake) would otherwise renew at the hourly cap all
    /// day, so a certificate is never renewed early more than once in that time.
    /// </summary>
    public void RenewalInfoChecked(DateTimeOffset? suggested, DateTimeOffset now)
    {
        RenewalInfoCheckedAt = now;
        if (suggested is { } at && at >= EarliestEarlyRenewal() && at < (RenewAfter ?? HalfLife()))
        {
            RenewAfter = at;
        }
    }

    public void FailureNotified(DateTimeOffset now) => FailureNotifiedAt = now;

    public void FailureNotificationCleared() => FailureNotifiedAt = null;

    public void ExpiryNotified(string sha256) => ExpiryNotifiedFor = sha256;

    private IEnumerable<DateTimeOffset> WithinTheHour(DateTimeOffset now) => RecentAttempts.Where(at => now - at < TimeSpan.FromHours(1));

    private void ForgetCertificate()
    {
        CertificateProtected = null;
        CertificateIp = null;
        CertificateDirectory = null;
        CertificateNotBefore = null;
        CertificateNotAfter = null;
        CertificateAriId = null;
        CertificateSha256 = null;
        IssuedAt = null;
        RenewAfter = null;
        RenewalInfoCheckedAt = null;
        ExpiryNotifiedFor = null;
    }

    private DateTimeOffset EarliestEarlyRenewal() =>
        (IssuedAt ?? CertificateNotBefore) is { } from && CertificateNotAfter is { } to && CertificateNotBefore is { } start
            ? from + ((to - start) / 3)
            : DateTimeOffset.MaxValue;

    private DateTimeOffset HalfLife() =>
        CertificateNotBefore is { } from && CertificateNotAfter is { } to ? from + ((to - from) / 2) : DateTimeOffset.MinValue;
}

public static class PublicIpCertificateErrors
{
    public static DomainError IpRequired { get; } = new("public_ip_certificate.ip_required", "Enter the panel's public IP address to turn this on.");

    public static DomainError IpInvalid { get; } =
        new("public_ip_certificate.ip_invalid", "Enter an IPv4 address (such as 203.0.113.10) or an IPv6 address, without a port or brackets.");

    public static DomainError IpNotPublic { get; } = new("public_ip_certificate.ip_not_public",
        "That is not a public internet address (it is private, local, shared by your provider or reserved). Use the address your router has on the internet.");

    public static DomainError EmailInvalid { get; } = new("public_ip_certificate.email_invalid", "The email must be a plain address, such as admin@example.com.");
}
