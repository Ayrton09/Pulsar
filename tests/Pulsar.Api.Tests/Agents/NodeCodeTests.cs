using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Nodes;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// The node code Add node shows for PulsarAgentSetup.exe (Phase 18): the token, the panel key and the panel's address as
/// the administrator reached it, and it is never kept.
/// </summary>
public sealed class NodeCodeApiTests(NodeCodeApiTests.Panel factory) : IClassFixture<NodeCodeApiTests.Panel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AddingANodeAlsoGivesTheCodeForItsSetup()
    {
        using var admin = await factory.CreateAdminClientAsync();

        var created = await NodeApi.CreateAsync(admin, "code-node");

        var enrollment = created.Enrollment;
        Assert.True(NodeCode.TryParse(enrollment.NodeCode, out var code));
        Assert.Equal("https://localhost", code.Url);
        Assert.Equal(enrollment.Token, code.Token);
        Assert.Equal(enrollment.PanelKeyFingerprint, code.PanelKey);
        // The test server has no TLS of its own to tell: the setup then needs a certificate the machine already trusts.
        Assert.Null(code.CertificateSha256);
        Assert.False(enrollment.AgentSetupAvailable);
        Assert.Null(enrollment.AgentSetupSha256);

        // What the code carries enrolls the machine, as the command does.
        await using var agent = new TestAgent(factory);
        await agent.EnrollAsync(code.Token, code.PanelKey);
        Assert.Equal(created.Node.Id, agent.NodeId);
    }

    [DatabaseFact]
    public async Task ANewEnrollmentCommandComesWithItsOwnCodeAndSaysWhetherTheSetupIsThere()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "code-again");
        var setup = Path.Combine(factory.DownloadsDirectory, "PulsarAgentSetup.exe");
        await File.WriteAllBytesAsync(setup, [0x4D, 0x5A], Ct);
        try
        {
            var reissued = await NewEnrollmentAsync(admin, created.Node.Id);

            Assert.True(reissued.AgentSetupAvailable);
            // For whoever downloads it to compare (docs/architecture/08, T37).
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([0x4D, 0x5A])), reissued.AgentSetupSha256);
            Assert.True(NodeCode.TryParse(reissued.NodeCode, out var code));
            Assert.Equal(reissued.Token, code.Token);
            Assert.NotEqual(created.Enrollment.Token, code.Token);
        }
        finally
        {
            File.Delete(setup);
        }
    }

    [DatabaseFact]
    public async Task NeitherTheTokenNorTheCodeIsAudited()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "code-audit");
        var reissued = await NewEnrollmentAsync(admin, created.Node.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var node = new NodeId(created.Node.Id);
        var entries = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.NodeId == node).ToListAsync(Ct);
        Assert.Equal(2, entries.Count(e => e.Action == AuditActions.Node.EnrollmentTokenCreated));
        string[] secrets =
        [
            created.Enrollment.Token,
            created.Enrollment.NodeCode![NodeCode.Prefix.Length..],
            reissued.Token,
            reissued.NodeCode![NodeCode.Prefix.Length..],
        ];
        Assert.All(entries, entry => Assert.All(secrets, secret =>
            Assert.DoesNotContain(secret, $"{entry.Metadata} {entry.TargetId} {entry.ActorDisplay} {entry.UserAgent}", StringComparison.Ordinal)));
    }

    private static async Task<EnrollmentInstructions> NewEnrollmentAsync(PanelClient admin, Guid nodeId)
    {
        using var response = await admin.PostAsync($"/api/v1/nodes/{nodeId}/enrollment-tokens");
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<EnrollmentInstructions>(AccessScenario.Json, Ct))!;
    }

    /// <summary>The panel with its own downloads folder, empty until a test puts the setup there.</summary>
    public sealed class Panel : PulsarApiFactory
    {
        public string DownloadsDirectory { get; } = Directory.CreateTempSubdirectory("pulsar-downloads-").FullName;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Downloads:Path", DownloadsDirectory);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Directory.Delete(DownloadsDirectory, recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>On real Kestrel with real TLS, the code carries the certificate the panel served the administrator.</summary>
public sealed class NodeCodeKestrelTests(KestrelPanelFactory factory) : IClassFixture<KestrelPanelFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TheCodeCarriesTheCertificateThePanelServed()
    {
        factory.StartServer();
        using var admin = await AdminAsync();
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(factory.CertificatePath, KestrelPanelFactory.CertificatePassword);
        var expected = NodeCode.CertificateHash(certificate.RawData);

        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            var (enrollment, answeredWith) = await CreateNodeAsync(admin, $"kestrel-code-{version.Major}", version);

            Assert.Equal(version, answeredWith);
            Assert.True(NodeCode.TryParse(enrollment.NodeCode, out var code));
            Assert.Equal($"https://127.0.0.1:{factory.MainPort}", code.Url);
            Assert.Equal(expected, code.CertificateSha256);
            Assert.Equal(enrollment.Token, code.Token);
        }
    }

    [DatabaseFact]
    public async Task BehindAProxyTheCodeCarriesNoCertificate()
    {
        factory.StartServer();
        using var admin = await AdminAsync();

        // The proxy (loopback is a trusted one) may have served the browser a certificate of its own.
        var (enrollment, _) = await CreateNodeAsync(admin, "kestrel-proxied", HttpVersion.Version11, ("X-Forwarded-For", "192.0.2.10"));

        Assert.True(NodeCode.TryParse(enrollment.NodeCode, out var code));
        Assert.Null(code.CertificateSha256);
    }

    private async Task<PanelClient> AdminAsync()
    {
        await factory.OnceAsync("admin", async () =>
        {
            using var setup = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);
            var token = factory.Services.GetRequiredService<InitialSetup>().IssueToken();
            using var response = await setup.PostAsync("/api/v1/auth/setup",
                new SetupRequest(token, PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword, "Administrator", null));
            await AccessScenario.EnsureSuccessAsync(response);
        });
        var admin = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);
        using var login = await admin.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword);
        await AccessScenario.EnsureSuccessAsync(login);
        await admin.GetSessionAsync();
        return admin;
    }

    private static async Task<(EnrollmentInstructions Enrollment, Version AnsweredWith)> CreateNodeAsync(
        PanelClient admin, string name, Version version, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/nodes", UriKind.Relative))
        {
            Content = JsonContent.Create(new CreateNodeRequest(name, null, null)),
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        foreach (var (header, value) in headers)
        {
            request.Headers.Add(header, value);
        }

        using var response = await admin.Http.SendAsync(request, Ct);
        await AccessScenario.EnsureSuccessAsync(response);
        var created = (await response.Content.ReadFromJsonAsync<CreatedNode>(AccessScenario.Json, Ct))!;
        return (created.Enrollment, response.Version);
    }
}

/// <summary>Which address a node code names, from the request that asked for it.</summary>
public sealed class PanelAddressTests
{
    [Theory]
    [InlineData("https", "panel.example.com:8443", "https://panel.example.com:8443")]
    [InlineData("https", "Panel.Example.COM", "https://panel.example.com")]
    [InlineData("https", "192.0.2.10:8443", "https://192.0.2.10:8443")]
    [InlineData("https", "[2001:DB8::1]:8443", "https://[2001:db8::1]:8443")]
    [InlineData("http", "localhost:5180", "http://localhost:5180")]
    [InlineData("http", "127.0.0.1:5180", "http://127.0.0.1:5180")]
    [InlineData("http", "[::1]:5180", "http://[::1]:5180")]
    public void TheCodeNamesTheOriginTheRequestReached(string scheme, string host, string url) =>
        Assert.Equal(new PanelAddress(url, null), HttpNodeSetup.AddressOf(Request(scheme, host)));

    [Theory]
    [InlineData("http", "panel.example.com")]
    [InlineData("http", "192.0.2.10:5180")]
    [InlineData("http", "loopback:5180")]
    [InlineData("https", "")]
    [InlineData("https", "admin@panel.example.com")]
    [InlineData("https", "panel.example.com:443")]
    [InlineData("http", "127.1:5180")]
    public void AnOriginAgentsCannotUseGivesNoCode(string scheme, string host) =>
        Assert.Null(HttpNodeSetup.AddressOf(Request(scheme, host)));

    [Fact]
    public void TheInstructionsNeverPrintTheirSecrets()
    {
        var printed = new EnrollmentInstructions("one-time-token", DateTimeOffset.UnixEpoch, "sha256:panel-key", "pulsar-node-1.the-code", "ab12").ToString();

        Assert.DoesNotContain("one-time-token", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("the-code", printed, StringComparison.Ordinal);
        Assert.Contains("PanelKeyFingerprint = sha256:panel-key", printed, StringComparison.Ordinal);
        Assert.Contains("AgentSetupAvailable = True", printed, StringComparison.Ordinal);
    }

    private static DefaultHttpContext Request(string scheme, string host)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        return http;
    }
}
