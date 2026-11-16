using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Tests.Sftp;

public sealed class SftpTestingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void AnAuthenticatorCodeSentJustBeforeTheBoundaryStillFitsTheNextWindow(int elapsedMilliseconds)
    {
        const string key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 22, 25, 59, 999, TimeSpan.Zero));
        var code = SftpTesting.AuthenticatorCode(key, clock);

        clock.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));

        // At 22:26:00 the code sent at 22:25:59.999 is still only one step old. The former helper generated
        // 22:25:29's code instead, which is now two steps old and is correctly refused by the panel.
        Assert.Equal(Totp.Compute(key, clock.GetUtcNow().AddSeconds(-Totp.StepSeconds)), code);
        Assert.NotEqual(Totp.Compute(key, clock.GetUtcNow().AddSeconds(-2 * Totp.StepSeconds)), code);
    }
}

/// <summary>Moves only the authenticator clock across a boundary after the helper has sent its enable request.</summary>
public sealed class AuthenticatorBoundaryApiFactory : PulsarApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new CrossBoundary(this)));
    }

    private sealed class CrossBoundary(AuthenticatorBoundaryApiFactory factory) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Path == "/api/v1/auth/two-factor/enable")
                {
                    factory.AuthenticatorClock.Advance(TimeSpan.FromMilliseconds(1));
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

public sealed class SftpTestingBoundaryTests(AuthenticatorBoundaryApiFactory factory) : IClassFixture<AuthenticatorBoundaryApiFactory>
{
    [DatabaseFact]
    public async Task TheHelperEnablesAcrossTheBoundaryAndLeavesFreshSingleUseCodes()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("helper-boundary-user");
        var clock = factory.AuthenticatorClock;
        clock.SetUtcNow(new DateTimeOffset(2090, 11, 16, 22, 25, 59, 999, TimeSpan.Zero));

        var (key, recoveryCodes) = await SftpTesting.EnableTwoFactorAsync(factory, scenario, "helper-boundary-user");

        Assert.Equal(10, recoveryCodes.Count);
        Assert.Equal(new DateTimeOffset(2090, 11, 16, 22, 26, 30, TimeSpan.Zero), clock.GetUtcNow());
        var loginCode = SftpTesting.AuthenticatorCode(key, clock);
        using var session = await SftpTesting.SignInWithCodeAsync(factory, "helper-boundary-user", key);
        Assert.Equal(Totp.Compute(key, clock.GetUtcNow().AddSeconds(-Totp.StepSeconds)), loginCode);

        using var browser = await PanelClient.CreateAsync(factory);
        using var password = await browser.LoginAsync("helper-boundary-user", AccessScenario.Password);
        await AccessScenario.EnsureSuccessAsync(password);
        using (var replayed = await browser.PostAsync("/api/v1/auth/login/two-factor", new TwoFactorLoginRequest(loginCode, null)))
        {
            await SetupTests.AssertProblemAsync(replayed, HttpStatusCode.Unauthorized, "auth.invalid_code");
        }

        using var fresh = await browser.PostAsync("/api/v1/auth/login/two-factor",
            new TwoFactorLoginRequest(SftpTesting.AuthenticatorCode(key, clock), null));
        await AccessScenario.EnsureSuccessAsync(fresh);
    }
}
