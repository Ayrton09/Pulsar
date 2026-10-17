using System.Net;
using Pulsar.Domain.Certificates;

namespace Pulsar.Domain.Tests.Certificates;

/// <summary>The public IP certificate's address rules and its renewal timing: never hammering Let's Encrypt.</summary>
public sealed class PublicIpCertificateSettingsTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.114.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2a00:1450:4001:82a::200e")]
    [InlineData("2001:200::1")]
    public void PublicAddressesAreTaken(string address)
    {
        Assert.True(PublicAddresses.TryParse(address, out var parsed));
        Assert.True(PublicAddresses.IsGloballyRoutable(parsed));
        Assert.Null(PublicIpCertificateSettings.Validate(true, address, null, out var normalized));
        Assert.Equal(IPAddress.Parse(address).ToString(), normalized);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.4")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("192.0.0.9")]
    [InlineData("192.0.2.10")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.10")]
    [InlineData("198.18.0.1")]
    [InlineData("192.88.99.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")]
    [InlineData("2001:1ff::1")]
    [InlineData("2002:c000:204::1")]
    [InlineData("3fff::1")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("100::1")]
    [InlineData("fec0::1")]
    public void NonPublicAddressesAreRefused(string address)
    {
        Assert.True(PublicAddresses.TryParse(address, out var parsed));
        Assert.False(PublicAddresses.IsGloballyRoutable(parsed));
        Assert.Equal(PublicIpCertificateErrors.IpNotPublic, PublicIpCertificateSettings.Validate(true, address, null, out _));
    }

    [Theory]
    [InlineData("panel.example.com")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3.256")]
    [InlineData("1.2.3.4:8443")]
    [InlineData("[2001:4860::1]")]
    [InlineData("2001:4860::1%3")]
    [InlineData("2001:4860::1/64")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData(" 1.2.3.4")]
    [InlineData("0x01020304")]
    [InlineData("16909060")]
    [InlineData("")]
    public void WhatIsNotAnAddressLiteralIsRefused(string text)
    {
        Assert.False(PublicAddresses.TryParse(text, out _));
        Assert.Equal(PublicIpCertificateErrors.IpInvalid, PublicIpCertificateSettings.Validate(true, text, null, out _));
    }

    [Fact]
    public void TurningItOnNeedsAnAddressAndAPlainEmail()
    {
        Assert.Equal(PublicIpCertificateErrors.IpRequired, PublicIpCertificateSettings.Validate(true, null, null, out _));
        Assert.Null(PublicIpCertificateSettings.Validate(false, null, null, out _));
        Assert.Equal(PublicIpCertificateErrors.EmailInvalid, PublicIpCertificateSettings.Validate(true, "1.2.3.4", "Admin <admin@example.com>", out _));
        Assert.Null(PublicIpCertificateSettings.Validate(true, "1.2.3.4", "admin@example.com", out _));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.2.3.4", true)]
    [InlineData("100.100.100.100", true)]
    [InlineData("192.168.0.2", true)]
    [InlineData("::ffff:192.168.0.2", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2a00:1450::1", false)]
    public void LocalClientsAreTold(string address, bool local) => Assert.Equal(local, PublicAddresses.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void TurningItOnAgreesToTheTermsAndAnotherAddressStartsAfresh()
    {
        var settings = PublicIpCertificateSettings.Default(s_now);
        Assert.False(settings.Enabled);
        Assert.Equal(AcmeDirectoryKind.Production, settings.Directory);

        Assert.True(settings.Change(true, "1.2.3.4", null, AcmeDirectoryKind.Production, s_now));
        Assert.Equal(s_now, settings.TermsAgreedAt);
        Assert.False(settings.Change(true, "1.2.3.4", null, AcmeDirectoryKind.Production, s_now.AddMinutes(1)));

        settings.AttemptStarted(s_now);
        settings.Failed("port 80 closed", null, s_now);
        Assert.Equal(1, settings.ConsecutiveFailures);
        Assert.True(settings.Change(true, "5.6.7.8", null, AcmeDirectoryKind.Production, s_now.AddMinutes(2)));
        Assert.Equal(0, settings.ConsecutiveFailures);
        Assert.Null(settings.NextAttemptAt);
        Assert.Null(settings.LastError);
        Assert.Equal(1, settings.AttemptsInWindow); // the hourly cap is not reset
        Assert.Equal(s_now, settings.TermsAgreedAt);
    }

    [Fact]
    public void FailuresBackOffFrom15MinutesDoublingUpToADay()
    {
        var waits = Enumerable.Range(1, 10).Select(PublicIpCertificateSettings.Backoff).ToList();

        Assert.Equal(
            [
                TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4),
                TimeSpan.FromHours(8), TimeSpan.FromHours(16), TimeSpan.FromHours(24), TimeSpan.FromHours(24), TimeSpan.FromHours(24),
            ],
            waits);
        Assert.Equal(TimeSpan.FromHours(24), PublicIpCertificateSettings.Backoff(1000));
    }

    [Fact]
    public void ARetryAfterLongerThanTheBackoffIsHonouredUpToAWeek()
    {
        var settings = Enabled();
        settings.Failed("rate limited", TimeSpan.FromHours(3), s_now);
        Assert.Equal(s_now.AddHours(3), settings.NextAttemptAt);

        settings.Failed("rate limited", TimeSpan.FromDays(30), s_now);
        Assert.Equal(s_now.AddDays(7), settings.NextAttemptAt);

        settings.Failed("short", TimeSpan.FromSeconds(1), s_now);
        Assert.Equal(s_now + PublicIpCertificateSettings.Backoff(3), settings.NextAttemptTime(s_now, forced: false));
    }

    [Fact]
    public void WithoutACertificateItTriesAtOnceThenWaitsTheBackoff()
    {
        var settings = Enabled();
        Assert.Equal(s_now, settings.NextAttemptTime(s_now, forced: false));

        settings.AttemptStarted(s_now);
        settings.Failed("nope", null, s_now);

        Assert.Equal(s_now.AddMinutes(15), settings.NextAttemptTime(s_now, forced: false));
        // The button skips the backoff...
        Assert.Equal(s_now, settings.NextAttemptTime(s_now, forced: true));
    }

    [Fact]
    public void AtMostThreeAttemptsStartInAnHourTheButtonsIncluded()
    {
        var settings = Enabled();
        settings.AttemptStarted(s_now);
        settings.AttemptStarted(s_now.AddMinutes(10));
        Assert.Equal(1, settings.AttemptsLeft(s_now.AddMinutes(10)));
        settings.AttemptStarted(s_now.AddMinutes(20));

        Assert.Equal(0, settings.AttemptsLeft(s_now.AddMinutes(30)));
        // ...but not the hourly cap.
        Assert.Equal(s_now.AddHours(1), settings.NextAttemptTime(s_now.AddMinutes(30), forced: true));
        Assert.Equal(PublicIpCertificateSettings.MaxAttemptsPerHour, settings.AttemptsLeft(s_now.AddHours(1)));

        settings.AttemptStarted(s_now.AddHours(1));
        Assert.Equal(2, settings.AttemptsLeft(s_now.AddHours(1)));
    }

    [Fact]
    public void ACertificateIsRenewedAtHalfItsLifetimeOrEarlierWhenAriSaysSo()
    {
        var settings = Enabled();
        settings.AttemptStarted(s_now);
        settings.Failed("first try failed", null, s_now);
        var notBefore = s_now.AddMinutes(-1);
        settings.Issued([1, 2, 3], "1.2.3.4", AcmeDirectoryKind.Production, notBefore, notBefore.AddHours(160), new string('a', 64), "a.b", s_now);

        Assert.True(settings.HasCurrentCertificate(s_now));
        Assert.Equal(0, settings.ConsecutiveFailures);
        Assert.Null(settings.LastError);
        Assert.Equal(notBefore.AddHours(80), settings.NextAttemptTime(s_now.AddHours(2), forced: false));

        settings.RenewalInfoChecked(notBefore.AddHours(200), s_now);
        Assert.Equal(notBefore.AddHours(80), settings.RenewAfter);
        settings.RenewalInfoChecked(s_now.AddHours(10), s_now);
        Assert.Equal(s_now.AddHours(10), settings.RenewAfter);

        // A failed renewal waits its backoff, not past what is due.
        settings.AttemptStarted(s_now.AddHours(10));
        settings.Failed("renewal failed", null, s_now.AddHours(10));
        Assert.Equal(s_now.AddHours(10).AddMinutes(15), settings.NextAttemptTime(s_now.AddHours(10), forced: false));

        // Another address: the certificate is for nothing configured, a new one is asked for at once.
        settings.Change(true, "5.6.7.8", null, AcmeDirectoryKind.Production, s_now.AddHours(11));
        Assert.False(settings.HasCurrentCertificate(s_now.AddHours(11)));
        Assert.Equal(s_now.AddHours(11), settings.NextAttemptTime(s_now.AddHours(11), forced: false));
    }

    [Fact]
    public void AnExpiredCertificateIsNotCurrent()
    {
        var settings = Enabled();
        settings.Issued([1], "1.2.3.4", AcmeDirectoryKind.Production, s_now.AddHours(-170), s_now.AddHours(-10), new string('b', 64), null, s_now);

        Assert.False(settings.HasCurrentCertificate(s_now));
        Assert.Equal(s_now, settings.NextAttemptTime(s_now, forced: false));
    }

    [Fact]
    public void ALongErrorIsCut()
    {
        var settings = Enabled();
        settings.Failed(new string('x', 2000), null, s_now);

        Assert.Equal(PublicIpCertificateSettings.MaxErrorLength, settings.LastError!.Length);
    }

    private static PublicIpCertificateSettings Enabled()
    {
        var settings = PublicIpCertificateSettings.Default(s_now);
        settings.Change(true, "1.2.3.4", null, AcmeDirectoryKind.Production, s_now);
        return settings;
    }
}
