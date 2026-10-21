using System.Net;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests.Auth;

/// <summary>
/// How long a browser session lasts (docs/architecture/08, "Users and web"): signing out ends every session of the user,
/// a cookie copied before included, and none lasts more than <see cref="SessionLifetime.MaxAge"/> however much it is used.
/// </summary>
public sealed class SessionLifetimeTests(TwoFactorApiFactory factory) : IClassFixture<TwoFactorApiFactory>
{
    [DatabaseFact]
    public async Task SigningOutEndsEveryCopyOfTheSession()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("lifetime-logout");
        using var user = await scenario.SignInAsync("lifetime-logout");
        using var copy = Copy(user);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(copy));

        using (var logout = await user.PostAsync("/api/v1/auth/logout"))
        {
            await AccessScenario.EnsureSuccessAsync(logout);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(copy));
    }

    [DatabaseFact]
    public async Task ASessionInUseStillEndsAfterItsMaximumAge()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("lifetime-age");
        using var user = await scenario.SignInAsync("lifetime-age");
        var step = TimeSpan.FromHours(5);

        // Used every few hours (each use renews the cookie): it lasts until its maximum age, not one request longer.
        var elapsed = TimeSpan.Zero;
        while (elapsed + step < SessionLifetime.MaxAge)
        {
            factory.Clock.Advance(step);
            elapsed += step;
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(user.Http));
        }

        factory.Clock.Advance(step);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(user.Http));
    }

    private HttpClient Copy(PanelClient client)
    {
        var copy = factory.CreateDefaultClient(new Uri("https://localhost"));
        copy.DefaultRequestHeaders.Add("Cookie", client.CookieHeader);
        return copy;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient http)
    {
        using var response = await http.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative), TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
