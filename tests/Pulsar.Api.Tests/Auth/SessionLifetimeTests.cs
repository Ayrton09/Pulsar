using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

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
    public async Task SigningOutWhileTheUserChangesMeanwhileStillEndsEveryCopy()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("lifetime-logout-race");
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        using var user = await SignInAsync(app, "lifetime-logout-race");
        using var copy = Copy(user);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(copy));

        // Right before the new stamp is saved, the user is changed and saved by someone else: its save fails, once.
        var changes = 0;
        hook.BeforeSave(SavesUser("lifetime-logout-race"), async () =>
        {
            changes++;
            await ChangeUserMeanwhileAsync("lifetime-logout-race");
        });

        using (var logout = await user.PostAsync("/api/v1/auth/logout"))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Equal(1, changes);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(copy));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(user.Http));
    }

    [DatabaseFact]
    public async Task ASignOutWhoseStampIsNeverStoredSaysSoAndEndsNothing()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("lifetime-logout-lost");
        var hook = new DatabaseHook();
        using var app = factory.WithDatabaseHook(hook);
        using var user = await SignInAsync(app, "lifetime-logout-lost");
        using var copy = Copy(user);

        // Every try is beaten by another change of the user.
        var changes = 0;
        void Arm() => hook.BeforeSave(SavesUser("lifetime-logout-lost"), async () =>
        {
            changes++;
            Arm();
            await ChangeUserMeanwhileAsync("lifetime-logout-lost");
        });
        Arm();

        using (var logout = await user.PostAsync("/api/v1/auth/logout"))
        {
            await SetupTests.AssertProblemAsync(logout, HttpStatusCode.Conflict, "auth.sign_out_failed");
        }

        Assert.Equal(SecurityStamps.MaxAttempts, changes);
        // Not announced as done, and not done: both copies still work (this one too, to try again).
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(copy));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(user.Http));

        hook.BeforeSave(_ => false, () => Task.CompletedTask);
        using (var again = await user.PostAsync("/api/v1/auth/logout"))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(copy));
    }

    private static Func<DbContext, bool> SavesUser(string userName) => context =>
        context.ChangeTracker.Entries<PulsarUser>().Any(e => e.State == EntityState.Modified && e.Entity.UserName == userName);

    /// <summary>A change of the user's row from elsewhere (a failed sign-in counted, say): its concurrency stamp moves on.</summary>
    private async Task ChangeUserMeanwhileAsync(string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var changed = await db.Users.Where(u => u.UserName == userName)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()), TestContext.Current.CancellationToken);
        Assert.Equal(1, changed);
    }

    private static async Task<PanelClient> SignInAsync(WebApplicationFactory<Program> app, string userName)
    {
        var client = await PanelClient.CreateAsync(app);
        using var login = await client.LoginAsync(userName, AccessScenario.Password);
        await AccessScenario.EnsureSuccessAsync(login);
        await client.GetSessionAsync();
        return client;
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

    [DatabaseFact]
    public async Task ARememberedSessionOutlivesTheBrowserAndLastsWhileItIsUsed()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("lifetime-remembered");
        using var user = await PanelClient.CreateAsync(factory);
        using (var login = await user.PostAsync("/api/v1/auth/login", new LoginRequest("lifetime-remembered", AccessScenario.Password, RememberMe: true)))
        {
            await AccessScenario.EnsureSuccessAsync(login);
            // A persistent cookie, for as long as the session may go unused.
            var expires = Expiry(login);
            Assert.NotNull(expires);
            Assert.InRange(expires!.Value - factory.Clock.GetUtcNow(), SessionLifetime.RememberFor - TimeSpan.FromMinutes(1),
                SessionLifetime.RememberFor + TimeSpan.FromMinutes(1));
        }

        // Used every few days, it lasts past the maximum age of the others.
        for (var day = 0; day < 40; day += 5)
        {
            factory.Clock.Advance(TimeSpan.FromDays(5));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(user.Http));
        }

        // Left unused for longer than it is remembered, it ends.
        factory.Clock.Advance(SessionLifetime.RememberFor + TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(user.Http));
    }

    [DatabaseFact]
    public async Task ARememberedSessionStillEndsWhenTheUserSignsOut()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("lifetime-remembered-out");
        using var user = await PanelClient.CreateAsync(factory);
        using (var login = await user.PostAsync("/api/v1/auth/login", new LoginRequest("lifetime-remembered-out", AccessScenario.Password, RememberMe: true)))
        {
            await AccessScenario.EnsureSuccessAsync(login);
        }

        await user.GetSessionAsync();
        using var copy = Copy(user);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(copy));
        using (var logout = await user.PostAsync("/api/v1/auth/logout"))
        {
            await AccessScenario.EnsureSuccessAsync(logout);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(copy));
    }

    [DatabaseFact]
    public async Task ASessionNotRememberedEndsWithTheBrowser()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("lifetime-browser");
        using var user = await PanelClient.CreateAsync(factory);
        using var login = await user.LoginAsync("lifetime-browser", AccessScenario.Password);
        await AccessScenario.EnsureSuccessAsync(login);

        Assert.Null(Expiry(login)); // a cookie without an expiry: the browser drops it when it closes
    }

    /// <summary>The expiry the answer gives the session cookie; null for a cookie that lasts as long as the browser.</summary>
    private static DateTimeOffset? Expiry(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.Contains("pulsar-session=", StringComparison.Ordinal));
        var expires = cookie.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        return expires is null ? null : DateTimeOffset.Parse(expires["expires=".Length..], System.Globalization.CultureInfo.InvariantCulture);
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
