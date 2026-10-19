using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests;

public sealed class RoutingTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Fact]
    public async Task LivenessEndpointReturnsOkWithoutTouchingTheDatabase()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api/v1/servers/unknown/route")]
    [InlineData("/hubs/unknown")]
    [InlineData("/agent/v1/unknown")]
    [InlineData("/downloads/unknown")]
    [InlineData("/downloads/PulsarAgentSetup.exe/unknown")]
    public async Task UnknownBackendRoutesReturnProblemDetailsInsteadOfTheSpa(string path)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// A page carries the Content-Security-Policy of the moment it was served (the Steam artwork setting changes it), so
    /// browsers must ask again every time, with its ETag, rather than reuse a copy with an old policy.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/servers")]
    [InlineData("/servers/0199f3a0-0000-7000-8000-000000000000/files")]
    public async Task PagesAreRevalidatedEveryTime(string path)
    {
        var webRoot = Directory.CreateTempSubdirectory("pulsar-webroot-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Pulsar</title>", TestContext.Current.CancellationToken);
            await using var panel = factory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
            using var client = panel.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            await AssertRevalidatedAsync(client, path, response);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    /// <summary>
    /// The built SPA's index.html and its fingerprinted copy, which MapStaticAssets would let browsers keep for a year,
    /// with the headers of a published panel (not the development ones of a build's manifest).
    /// </summary>
    [Fact]
    public async Task TheBuiltIndexIsRevalidatedEveryTimeToo()
    {
        await using var panel = factory.WithWebHostBuilder(builder => builder.UseSetting("EnableStaticAssetsDevelopmentCaching", "true"));
        using var client = panel.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var routes = panel.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(r => r.StartsWith("index.", StringComparison.Ordinal) && r.EndsWith(".html", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.SkipWhen(routes.Count == 0, "The SPA is not built (src/Pulsar.Web: npm run build).");

        foreach (var route in routes)
        {
            using var response = await client.GetAsync(new Uri("/" + route, UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await AssertRevalidatedAsync(client, "/" + route, response);
        }
    }

    [Fact]
    public async Task ServerErrorsKeepTheHardeningHeaders()
    {
        // Without a database the session endpoint fails: the exception handler replaces the response with a 500.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync(new Uri("/api/v1/auth/session", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("default-src 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    /// <summary>Kept only to be asked again: so the page and, asked with its ETag, the "not modified" answer say.</summary>
    private static async Task AssertRevalidatedAsync(HttpClient client, string path, HttpResponseMessage response)
    {
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var etag = Assert.IsType<EntityTagHeaderValue>(response.Headers.ETag);
        Assert.Contains("default-src 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);

        using var again = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        again.Headers.IfNoneMatch.Add(etag);
        using var notModified = await client.SendAsync(again, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal("no-cache", notModified.Headers.CacheControl?.ToString());
        Assert.Contains("default-src 'self'", Assert.Single(notModified.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }
}
