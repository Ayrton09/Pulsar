using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests.Auth;

public sealed class AccountTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task UsersEditTheirOwnProfile()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("profile-user");
        using var user = await scenario.SignInAsync("profile-user");

        using var response = await user.Http.PutAsJsonAsync(new Uri("/api/v1/auth/profile", UriKind.Relative),
            new UpdateProfileRequest("Profile User", "profile@example.com"), TestContext.Current.CancellationToken);

        await AccessScenario.EnsureSuccessAsync(response);
        Assert.Equal("Profile User", (await user.GetSessionAsync()).User!.DisplayName);
    }

    [DatabaseFact]
    public async Task ChangingThePasswordKeepsThisSessionAndEndsTheOthers()
    {
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero)));
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("password-user");
        using var here = await SignInAsync(app, "password-user", AccessScenario.Password);
        using var elsewhere = await SignInAsync(app, "password-user", AccessScenario.Password);

        using (var wrong = await here.PostAsync("/api/v1/auth/password", new ChangePasswordRequest("not my password", "the new password here")))
        {
            await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.wrong_current_password");
        }

        using (var changed = await here.PostAsync("/api/v1/auth/password", new ChangePasswordRequest(AccessScenario.Password, "the new password here")))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }

        using (var stillHere = await here.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, stillHere.StatusCode);
        }

        using (var endedElsewhere = await elsewhere.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, endedElsewhere.StatusCode);
        }

        using var again = await SignInAsync(app, "password-user", "the new password here");
    }

    [DatabaseFact]
    public async Task WeakNewPasswordsAreRejected()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("weak-password-user");
        using var user = await scenario.SignInAsync("weak-password-user");

        using var response = await user.PostAsync("/api/v1/auth/password", new ChangePasswordRequest(AccessScenario.Password, "short"));

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation.failed");
    }

    private static async Task<PanelClient> SignInAsync(WebApplicationFactory<Program> app, string userName, string password)
    {
        var client = await PanelClient.CreateAsync(app);
        using var login = await client.LoginAsync(userName, password);
        await AccessScenario.EnsureSuccessAsync(login);
        await client.GetSessionAsync();
        return client;
    }
}

public sealed class SecurityHeadersTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/")]
    public async Task EveryResponseCarriesTheHardeningHeaders(string path)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Equal(1, csp.Split("unsafe-inline").Length - 1); // only style-src-elem (see SecurityHeaders)
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
}
