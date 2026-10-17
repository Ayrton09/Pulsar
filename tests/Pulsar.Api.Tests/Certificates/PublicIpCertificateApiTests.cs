using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulsar.Api.Certificates;
using Pulsar.Api.Notifications;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Api.Tests.Notifications;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Notifications;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Certificates;
using Pulsar.Domain.Notifications;

namespace Pulsar.Api.Tests.Certificates;

/// <summary>
/// The panel with its ACME client pointed at <see cref="FakeAcmeServer"/> and its http-01 responder on a loopback port
/// (never Let's Encrypt, never port 80), and notifications recorded instead of sent.
/// </summary>
public class PublicIpCertificatePanel : PulsarApiFactory
{
    public FakeAcmeServer Acme { get; } = FakeAcmeServer.StartAsync().GetAwaiter().GetResult();

    public FakeNotificationTransport Email { get; } = new(NotificationKind.Email);

    internal PublicIpCertificateService Service => Services.GetRequiredService<PublicIpCertificateService>();

    internal PublicIpCertificateStore Store => Services.GetRequiredService<PublicIpCertificateStore>();

    internal AcmeChallengeListener Listener => Services.GetRequiredService<AcmeChallengeListener>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Acme.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<PublicIpCertificateOptions>(options =>
            {
                options.DirectoryOverride = Acme.DirectoryUrl.AbsoluteUri;
                options.HandlerOverride = Acme.Handler;
                options.TrustedRootsOverride = new X509Certificate2Collection(Acme.Authority.Root);
                options.ChallengeBindAddress = "127.0.0.1";
                options.ChallengePortOverride = 0;
                options.PollInterval = TimeSpan.FromMilliseconds(20);
                options.ValidationTimeout = TimeSpan.FromSeconds(30);
            });
            services.RemoveAll<INotificationTransport>();
            services.AddSingleton<INotificationTransport>(Email);
            services.AddSingleton<INotificationTransport>(new FakeNotificationTransport(NotificationKind.Discord));
        });
    }

    /// <summary>The fake server checks the challenge at this panel's responder, wherever it listens.</summary>
    public void PointAcmeAtTheResponder() => Acme.ResponderPortSource = () => Listener.ListeningPort;
}

/// <summary>The public IP certificate's settings: who may see and change them, and what they take.</summary>
public sealed class PublicIpCertificateSettingsApiTests(PublicIpCertificatePanel factory) : IClassFixture<PublicIpCertificatePanel>
{
    private const string Path = "/api/v1/public-ip-certificate";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task OnlySettingsManagersSeeOrChangeIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var viewer = await scenario.SignedInWithAsync("certificate-viewer");

        using (var forbidden = await viewer.Http.GetAsync(Path, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var forbidden = await viewer.PutAsync(Path, new UpdatePublicIpCertificateRequest(true, "1.2.3.4", null)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var forbidden = await viewer.PostAsync(Path + "/renew", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var status = await GetAsync(scenario.Admin);
        Assert.False(status.Enabled);
        Assert.Equal(AcmeDirectoryKind.Production, status.Directory);
        Assert.Equal(PublicIpCertificateEndpoints.LetsEncryptRepository, status.TermsOfServiceUrl);
        Assert.Equal(80, status.ChallengePort);
        Assert.False(status.Listening);
        Assert.Null(status.Certificate);
        Assert.Null(factory.Listener.ListeningPort);
    }

    [DatabaseTheory]
    [InlineData(true, "192.168.1.10", null, "public_ip_certificate.ip_not_public")]
    [InlineData(true, "100.64.0.1", null, "public_ip_certificate.ip_not_public")]
    [InlineData(true, "127.0.0.1", null, "public_ip_certificate.ip_not_public")]
    [InlineData(true, "fd00::1", null, "public_ip_certificate.ip_not_public")]
    [InlineData(true, "2001:db8::1", null, "public_ip_certificate.ip_not_public")]
    [InlineData(true, "panel.example.com", null, "public_ip_certificate.ip_invalid")]
    [InlineData(true, "1.2.3.4:8443", null, "public_ip_certificate.ip_invalid")]
    [InlineData(true, null, null, "public_ip_certificate.ip_required")]
    [InlineData(true, "1.2.3.4", "not an email", "public_ip_certificate.email_invalid")]
    [InlineData(false, "10.0.0.1", null, "public_ip_certificate.ip_not_public")]
    public async Task WhatIsNotAPublicAddressIsRefused(bool enabled, string? ip, string? email, string code)
    {
        using var admin = await factory.CreateAdminClientAsync();

        using var refused = await admin.PutAsync(Path, new UpdatePublicIpCertificateRequest(enabled, ip, email));

        await Auth.SetupTests.AssertProblemAsync(refused, HttpStatusCode.BadRequest, code);
    }

    [DatabaseFact]
    public async Task RenewingNeedsItOn()
    {
        using var admin = await factory.CreateAdminClientAsync();

        using var refused = await admin.PostAsync(Path + "/renew", new { });

        await Auth.SetupTests.AssertProblemAsync(refused, HttpStatusCode.Conflict, "public_ip_certificate.disabled");
    }

    private static async Task<PublicIpCertificateResponse> GetAsync(PanelClient admin) =>
        (await admin.Http.GetFromJsonAsync<PublicIpCertificateResponse>(Path, AccessScenario.Json, Ct))!;
}

/// <summary>Turned on, the panel gets a certificate from the ACME server, serves it, renews it and stops when turned off.</summary>
public sealed class PublicIpCertificateRenewalTests(PublicIpCertificatePanel factory) : IClassFixture<PublicIpCertificatePanel>
{
    private const string Path = "/api/v1/public-ip-certificate";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TurnedOnItGetsACertificateServesItRenewsItAndStopsWhenTurnedOff()
    {
        factory.PointAcmeAtTheResponder();
        using var admin = await factory.CreateAdminClientAsync();

        using (var on = await admin.PutAsync(Path, new UpdatePublicIpCertificateRequest(true, "1.2.3.4", "admin@example.com")))
        {
            await AccessScenario.EnsureSuccessAsync(on);
        }

        var status = await EventuallyAsync(admin, s => s is { Serving: true, Certificate: not null }, "a certificate served");
        Assert.True(status.Listening);
        Assert.Equal("1.2.3.4", status.Certificate!.Ip);
        Assert.Equal(TimeSpan.FromHours(160), status.Certificate.NotAfter - status.Certificate.NotBefore);
        Assert.False(status.Certificate.Staging);
        Assert.Null(status.LastError);
        Assert.NotNull(status.LastRenewalAt);
        Assert.Equal(status.Certificate.NotBefore.AddHours(80), status.NextAttemptAt);
        Assert.Equal(status.Certificate.Sha256, factory.Store.Current!.Sha256);
        Assert.Equal("https://letsencrypt.example/documents/subscriber-agreement.pdf", status.TermsOfServiceUrl);

        // What Let's Encrypt was asked: an account agreeing to its terms, and the address with the shortlived profile.
        Assert.True(Assert.Single(factory.Acme.NewAccounts)["termsOfServiceAgreed"]!.GetValue<bool>());
        var order = Assert.Single(factory.Acme.NewOrders);
        Assert.Equal("shortlived", order["profile"]!.GetValue<string>());
        Assert.Equal("1.2.3.4", order["identifiers"]![0]!["value"]!.GetValue<string>());

        // The account key and the certificate's key are kept encrypted; the change is audited with the agreement.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            var row = await db.PublicIpCertificateSettings.AsNoTracking().SingleAsync(Ct);
            Assert.NotNull(row.AccountKeyProtected);
            Assert.NotNull(row.AccountUrl);
            Assert.DoesNotContain("BEGIN", Encoding.Latin1.GetString(row.CertificateProtected!), StringComparison.Ordinal);
            Assert.Single(row.RecentAttempts);
            var audited = await db.AuditLog.AsNoTracking().Where(e => e.Action == AuditActions.PublicIpCertificate.SettingsChanged).ToListAsync(Ct);
            Assert.Contains(audited, e => System.Text.Json.JsonDocument.Parse(e.Metadata).RootElement.EnumerateObject()
                .Any(p => p.NameEquals("termsAgreed") && p.Value.ValueKind == System.Text.Json.JsonValueKind.True));
        }

        // Renew now: a new certificate, ordered as the replacement of the first (ARI).
        var first = status.Certificate.Sha256;
        using (var renew = await admin.PostAsync(Path + "/renew", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, renew.StatusCode);
        }

        status = await EventuallyAsync(admin, s => s.Certificate?.Sha256 is { } sha && sha != first && s.Serving, "a renewed certificate");
        Assert.Equal(2, factory.Acme.NewOrders.Count);
        Assert.NotNull(factory.Acme.NewOrders[1]["replaces"]);
        Assert.Equal(status.Certificate!.Sha256, factory.Store.Current!.Sha256);
        Assert.Equal(1, status.AttemptsLeft);

        // Off: nothing served, nothing listening on the challenge port.
        using (var off = await admin.PutAsync(Path, new UpdatePublicIpCertificateRequest(false, "1.2.3.4", null)))
        {
            await AccessScenario.EnsureSuccessAsync(off);
        }

        Assert.Null(factory.Store.Current);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Listener.ListeningPort is null), "the challenge port closed");
        status = await GetAsync(admin);
        Assert.False(status.Serving);
        Assert.False(status.Listening);
        // Its key is not kept either.
        Assert.Null(status.Certificate);
        await using var after = factory.Services.CreateAsyncScope();
        Assert.Null((await after.ServiceProvider.GetRequiredService<IPulsarDbContext>().PublicIpCertificateSettings.AsNoTracking().SingleAsync(Ct)).CertificateProtected);
    }

    private static async Task<PublicIpCertificateResponse> GetAsync(PanelClient admin) =>
        (await admin.Http.GetFromJsonAsync<PublicIpCertificateResponse>(Path, AccessScenario.Json, Ct))!;

    private static async Task<PublicIpCertificateResponse> EventuallyAsync(PanelClient admin, Func<PublicIpCertificateResponse, bool> condition, string what)
    {
        PublicIpCertificateResponse? last = null;
        await InProcessAgent.EventuallyAsync(async () => condition(last = await GetAsync(admin)), () => $"{what} (last: {last})");
        return last!;
    }
}

/// <summary>When Let's Encrypt cannot check port 80: the failure is shown and notified, backed off, and capped per hour.</summary>
public sealed class PublicIpCertificateFailureTests(PublicIpCertificatePanel factory) : IClassFixture<PublicIpCertificatePanel>
{
    private const string Path = "/api/v1/public-ip-certificate";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AFailedAttemptIsShownNotifiedBackedOffAndTheButtonIsCappedPerHour()
    {
        // Let's Encrypt cannot reach the responder (the router does not forward port 80).
        factory.Acme.ResponderPortSource = () => 1;
        using var admin = await factory.CreateAdminClientAsync();
        using (var channel = await admin.PostAsync("/api/v1/notifications/channels", new NotificationChannelRequest("certificates", NotificationKind.Email, true,
            [NotificationEvent.PublicIpCertificate], null, "es", null, ["ops@example.com"])))
        {
            await AccessScenario.EnsureSuccessAsync(channel);
        }

        using (var on = await admin.PutAsync(Path, new UpdatePublicIpCertificateRequest(true, "1.2.3.4", null)))
        {
            await AccessScenario.EnsureSuccessAsync(on);
        }

        var status = await EventuallyAsync(admin, s => s.LastError is not null && !s.Renewing, "a failed attempt");
        Assert.Contains("could not check port 80 of 1.2.3.4", status.LastError, StringComparison.Ordinal);
        Assert.Null(status.Certificate);
        Assert.False(status.Serving);
        Assert.Null(factory.Store.Current);
        Assert.Equal(status.LastErrorAt!.Value + PublicIpCertificateSettings.FirstBackoff, status.NextAttemptAt);
        Assert.True(status.LastErrorAt >= status.LastAttemptAt);
        Assert.Equal(2, status.AttemptsLeft);

        // Told once, in the channel's language.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Email.Sent.Any(m => m.Channel == "certificates")), "the failure notification");
        var told = Assert.Single(factory.Email.Sent, m => m.Channel == "certificates");
        Assert.Equal("No se pudo renovar el certificado de la IP pública", told.Message.Title);
        Assert.Equal(NotificationSeverity.Error, told.Message.Severity);

        // The button skips the backoff, two more times this hour; then it is refused until the hour ends.
        foreach (var left in new[] { 1, 0 })
        {
            using (var renew = await admin.PostAsync(Path + "/renew", new { }))
            {
                Assert.Equal(HttpStatusCode.Accepted, renew.StatusCode);
            }

            status = await EventuallyAsync(admin, s => s.AttemptsLeft == left && !s.Renewing && s.LastError is not null, $"attempt with {left} left");
        }

        using (var refused = await admin.PostAsync(Path + "/renew", new { }))
        {
            await Auth.SetupTests.AssertProblemAsync(refused, HttpStatusCode.Conflict, "public_ip_certificate.too_many_attempts");
        }

        Assert.Equal(3, factory.Acme.NewOrders.Count);
        // Still one notification: the next one comes 12 hours after it at the earliest.
        Assert.Single(factory.Email.Sent, m => m.Channel == "certificates");
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().PublicIpCertificateSettings.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(3, row.ConsecutiveFailures);
        Assert.Equal(row.LastErrorAt!.Value + TimeSpan.FromHours(1), row.NextAttemptAt);
    }

    private static async Task<PublicIpCertificateResponse> GetAsync(PanelClient admin) =>
        (await admin.Http.GetFromJsonAsync<PublicIpCertificateResponse>(Path, AccessScenario.Json, Ct))!;

    private static async Task<PublicIpCertificateResponse> EventuallyAsync(PanelClient admin, Func<PublicIpCertificateResponse, bool> condition, string what)
    {
        PublicIpCertificateResponse? last = null;
        await InProcessAgent.EventuallyAsync(async () => condition(last = await GetAsync(admin)), () => $"{what} (last: {last})");
        return last!;
    }
}
