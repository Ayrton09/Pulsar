using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Pulsar.Api.Tests;

public sealed class RoutingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task LivenessEndpointReturnsOk()
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
    public async Task UnknownBackendRoutesReturnProblemDetailsInsteadOfTheSpa(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
