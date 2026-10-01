using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests.Auth;

public sealed class HttpsRequiredTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Fact]
    public async Task ApiRefusesPlainHttpWithAClearProblem()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });

        using var response = await client.GetAsync(new Uri("/api/v1/auth/session", UriKind.Relative), TestContext.Current.CancellationToken);

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "transport.https_required");
    }
}

public sealed class InsecureHttpOptInTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task PlainHttpWorksWhenAllowedAndTheSessionSaysSo()
    {
        using var insecure = factory.WithWebHostBuilder(b => b.UseSetting("Security:AllowInsecureHttp", "true"));
        using var client = insecure.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });

        using var response = await client.GetAsync(new Uri("/api/v1/auth/session", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken);
        Assert.True(session!.InsecureConnection);

        // No __Host- prefix and no Secure attribute: browsers would drop such cookies on plain HTTP.
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.StartsWith("pulsar-xsrf=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("pulsar-af=", StringComparison.Ordinal));
        Assert.All(cookies, c => Assert.DoesNotContain("secure", c, StringComparison.OrdinalIgnoreCase));
    }

    [DatabaseFact]
    public async Task HttpsSessionsUseHostPrefixedSecureCookies()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync(new Uri("/api/v1/auth/session", UriKind.Relative), TestContext.Current.CancellationToken);

        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(TestContext.Current.CancellationToken);
        Assert.False(session!.InsecureConnection);
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.StartsWith("__Host-pulsar-xsrf=", StringComparison.Ordinal));
        Assert.All(cookies, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));
    }
}
