using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Certificates;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Tests.Certificates;

/// <summary>
/// The panel with the renewal loop off, so a test drives each look at the settings itself, and a way to make the ACME
/// client's requests fail with whatever exception the test names.
/// </summary>
public sealed class PublicIpCertificateManualPanel : PublicIpCertificatePanel
{
    /// <summary>When set, every request of the ACME client throws it.</summary>
    public Exception? Fault { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.PostConfigure<PublicIpCertificateOptions>(options =>
        {
            options.Run = false;
            options.HandlerOverride = () => new FaultyHandler(Acme.Handler(), () => Fault);
        }));
    }

    private sealed class FaultyHandler(HttpMessageHandler inner, Func<Exception?> fault) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            fault() is { } error ? throw error : base.SendAsync(request, cancellationToken);
    }
}

/// <summary>What the renewal does when an attempt goes wrong in ways the protocol does not name, or the settings change under it.</summary>
public sealed class PublicIpCertificateServiceTests(PublicIpCertificateManualPanel factory) : IClassFixture<PublicIpCertificateManualPanel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AnUnexpectedExceptionIsAFailedAttemptLikeAnyOther()
    {
        await EnableAsync();
        factory.Fault = new InvalidOperationException("boom");

        await factory.Service.RunOnceAsync(Ct);

        var row = await RowAsync();
        Assert.Equal(1, row.ConsecutiveFailures);
        Assert.Contains("InvalidOperationException", row.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", row.LastError, StringComparison.Ordinal);
        Assert.NotNull(row.NextAttemptAt);
        Assert.NotNull(row.FailureNotifiedAt);
        factory.Fault = null;
    }

    [DatabaseFact]
    public async Task AnAriFailureOfAnyKindDoesNotBlockTheRenewal()
    {
        factory.PointAcmeAtTheResponder();
        await EnableAsync();
        await factory.Service.RunOnceAsync(Ct);
        Assert.NotNull((await RowAsync()).CertificateProtected);

        factory.Fault = new InvalidOperationException("boom");
        try
        {
            var wait = await factory.Service.RunOnceAsync(Ct);

            Assert.True(wait > TimeSpan.Zero);
            Assert.NotNull((await RowAsync()).RenewalInfoCheckedAt);
        }
        finally
        {
            factory.Fault = null;
        }
    }

    [DatabaseFact]
    public async Task AnAccountKeyNothingCanReadIsReplacedAndTheAccountRegisteredAgain()
    {
        factory.PointAcmeAtTheResponder();
        byte[] lost = [1, 2, 3];
        await EnableAsync(settings =>
        {
            settings.SetAccountKey(lost);
            settings.AccountRegistered("https://elsewhere.example/acct/1", AcmeDirectoryKind.Production, "", null);
        });

        await factory.Service.RunOnceAsync(Ct);

        var row = await RowAsync();
        Assert.NotEqual(lost, row.AccountKeyProtected);
        Assert.NotNull(row.CertificateProtected);
        Assert.StartsWith(factory.Acme.BaseUrl.AbsoluteUri, row.AccountUrl, StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        var audited = await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.PublicIpCertificate.AccountKeyReplaced, Ct);
        Assert.Equal(AuditActorType.System, audited.ActorType);
    }

    [DatabaseFact]
    public async Task AnIssuedCertificateIsNotKeptWhenItWasTurnedOffMeanwhile()
    {
        factory.PointAcmeAtTheResponder();
        await EnableAsync();
        factory.Acme.Intercept = async context =>
        {
            if (context.Request.Path.StartsWithSegments("/finalize", StringComparison.Ordinal))
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
                var settings = await db.PublicIpCertificateSettings.SingleAsync(Ct);
                settings.Change(false, "1.2.3.4", null, AcmeDirectoryKind.Production, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(Ct);
            }

            return false;
        };

        try
        {
            await factory.Service.RunOnceAsync(Ct);
        }
        finally
        {
            factory.Acme.Intercept = null;
        }

        var row = await RowAsync();
        Assert.False(row.Enabled);
        Assert.Null(row.CertificateProtected);
        Assert.Null(row.CertificateSha256);
        Assert.Null(factory.Store.Current);
    }

    private async Task EnableAsync(Action<PublicIpCertificateSettings>? seed = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        await db.PublicIpCertificateSettings.ExecuteDeleteAsync(Ct);
        factory.Store.Serve(null);
        var now = DateTimeOffset.UtcNow;
        var settings = PublicIpCertificateSettings.Default(now);
        seed?.Invoke(settings);
        settings.Change(true, "1.2.3.4", null, AcmeDirectoryKind.Production, now);
        db.PublicIpCertificateSettings.Add(settings);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<PublicIpCertificateSettings> RowAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().PublicIpCertificateSettings.AsNoTracking().SingleAsync(Ct);
    }
}
