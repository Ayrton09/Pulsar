using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Pulsar.Api.Nodes;
using Pulsar.Api.Tests.Auth;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests;

/// <summary>
/// <c>GET /downloads/PulsarAgentSetup.exe</c> (Phase 18): the agent's setup for machines added as nodes, anonymous, one
/// fixed file from one fixed folder, and nothing else from it.
/// </summary>
public sealed class AgentSetupDownloadTests : IClassFixture<NoDatabaseApiFactory>, IAsyncDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pulsar-downloads-").FullName;
    private readonly WebApplicationFactory<Program> _panel;

    public AgentSetupDownloadTests(NoDatabaseApiFactory factory) =>
        _panel = factory.WithWebHostBuilder(builder => builder.UseSetting("Downloads:Path", _folder));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSetupIsServedToAnyoneAsAnAttachment()
    {
        byte[] setup = [0x4D, 0x5A, .. RandomNumberGenerator.GetBytes(4096)];
        await File.WriteAllBytesAsync(Path.Combine(_folder, "PulsarAgentSetup.exe"), setup, Ct);
        using var client = Client("https://localhost");

        using var response = await client.GetAsync(new Uri("/downloads/PulsarAgentSetup.exe", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(setup, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("PulsarAgentSetup.exe", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task WithoutTheSetupTheAnswerIsNotFound()
    {
        using var client = Client("https://localhost");

        using var response = await client.GetAsync(new Uri("/downloads/PulsarAgentSetup.exe", UriKind.Relative), Ct);

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.NotFound, "downloads.not_found");
    }

    [Fact]
    public async Task AFolderNamedLikeTheSetupIsNotServed()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "PulsarAgentSetup.exe"));
        using var client = Client("https://localhost");

        using var response = await client.GetAsync(new Uri("/downloads/PulsarAgentSetup.exe", UriKind.Relative), Ct);

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.NotFound, "downloads.not_found");
    }

    [Theory]
    [InlineData("/downloads")]
    [InlineData("/downloads/")]
    [InlineData("/downloads/secret.txt")]
    [InlineData("/downloads/other.exe")]
    [InlineData("/downloads/PulsarAgentSetup")]
    [InlineData("/downloads/PulsarAgentSetup.exe/more")]
    [InlineData("/downloads/sub/PulsarAgentSetup.exe")]
    [InlineData("/downloads/..%2Fappsettings.json")]
    [InlineData("/downloads/%2e%2e/appsettings.json")]
    public async Task NothingElseIsServedFromThere(string path)
    {
        await File.WriteAllBytesAsync(Path.Combine(_folder, "PulsarAgentSetup.exe"), [0x4D, 0x5A], Ct);
        await File.WriteAllTextAsync(Path.Combine(_folder, "secret.txt"), "not for download", Ct);
        await File.WriteAllBytesAsync(Path.Combine(_folder, "other.exe"), [0x4D, 0x5A], Ct);
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(_folder, "sub", "PulsarAgentSetup.exe"), [0x4D, 0x5A], Ct);
        using var client = Client("https://localhost");

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        // Never the file, and never the SPA's index.html.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PlainHttpIsRefused()
    {
        await File.WriteAllBytesAsync(Path.Combine(_folder, "PulsarAgentSetup.exe"), [0x4D, 0x5A], Ct);
        using var client = Client("http://localhost");

        using var response = await client.GetAsync(new Uri("/downloads/PulsarAgentSetup.exe", UriKind.Relative), Ct);

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "transport.https_required");
    }

    [Fact]
    public void TheFolderIsDownloadsNextToThePanelUnlessSet()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "downloads", "PulsarAgentSetup.exe"),
            new AgentSetupDownload(Options.Create(new DownloadsOptions())).FilePath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "setups", "PulsarAgentSetup.exe"),
            new AgentSetupDownload(Options.Create(new DownloadsOptions { Path = "setups" })).FilePath);
        Assert.Equal(Path.Combine(_folder, "PulsarAgentSetup.exe"),
            new AgentSetupDownload(Options.Create(new DownloadsOptions { Path = _folder })).FilePath);
    }

    public async ValueTask DisposeAsync()
    {
        await _panel.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private HttpClient Client(string baseAddress) =>
        _panel.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false });
}
