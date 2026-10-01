using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests.Auth;

/// <summary>
/// Disabling a user or resetting their password must end the sessions they already have. The panel revalidates the
/// cookie every minute; here the interval is zero so the next request already sees the change.
/// </summary>
public sealed class SessionRevocationTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task DisablingAUserEndsTheirOpenSession()
    {
        using var app = WithImmediateRevalidation();
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("revoked-by-disable");
        using var session = await SignInAsync(app, "revoked-by-disable");

        using var disable = await admin.Http.PutAsJsonAsync(new Uri($"/api/v1/users/{user.Id}", UriKind.Relative),
            new UpdateUserRequest(null, null, IsDisabled: true), TestContext.Current.CancellationToken);
        await AccessScenario.EnsureSuccessAsync(disable);

        using var me = await session.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [DatabaseFact]
    public async Task PasswordResetEndsTheirOpenSession()
    {
        using var app = WithImmediateRevalidation();
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var user = await scenario.CreateUserAsync("revoked-by-reset");
        using var session = await SignInAsync(app, "revoked-by-reset");

        using var reset = await admin.PostAsync($"/api/v1/users/{user.Id}/password", new ResetPasswordRequest("a completely new password"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var me = await session.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithImmediateRevalidation() =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero)));

    private static async Task<PanelClient> SignInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string userName)
    {
        var client = await PanelClient.CreateAsync(app);
        using var login = await client.LoginAsync(userName, AccessScenario.Password);
        await AccessScenario.EnsureSuccessAsync(login);
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        return client;
    }
}
