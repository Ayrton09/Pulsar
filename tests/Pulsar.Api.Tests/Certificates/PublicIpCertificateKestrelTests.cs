using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Certificates;
using Pulsar.Api.Nodes;
using Pulsar.Api.Security;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Certificates;

/// <summary>
/// The certificate each connection gets on real Kestrel: the public IP certificate on the main endpoint for a connection
/// from the internet that named no host (here the loopback stands for the internet), the installer's certificate for one
/// that named a host and on the agents' endpoint always; and a node code made over it names no certificate to pin.
/// </summary>
public sealed class PublicIpCertificateKestrelTests(PublicIpCertificateKestrelTests.Panel factory) : IClassFixture<PublicIpCertificateKestrelTests.Panel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task InternetConnectionsGetThePublicCertificateOnTheMainEndpointOnly()
    {
        factory.StartServer();
        await SetUpAdministratorAsync();
        var store = factory.Services.GetRequiredService<PublicIpCertificateStore>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // A leaf without intermediates: serving a chain on Windows would put the intermediate in this account's CA store.
        using var leaf = Leaf(key);
        store.Serve(ServedPublicIpCertificate.Create([leaf], key, IPAddress.Loopback));
        try
        {
            Assert.Equal(leaf.Thumbprint, await PresentedAsync(factory.MainPort, "127.0.0.1"));
            Assert.Equal(factory.ServerThumbprint, await PresentedAsync(factory.MainPort, "localhost"));
            Assert.Equal(factory.ServerThumbprint, await PresentedAsync(factory.AgentsPort, "127.0.0.1"));

            // The administrator reached the panel over the public certificate: the code pins nothing.
            using var admin = await AdminAsync(leaf.Thumbprint);
            using var created = await admin.PostAsync("/api/v1/nodes", new CreateNodeRequest("public-ip-node", null, null));
            await AccessScenario.EnsureSuccessAsync(created);
            var enrollment = (await created.Content.ReadFromJsonAsync<CreatedNode>(AccessScenario.Json, Ct))!.Enrollment;
            Assert.True(NodeCode.TryParse(enrollment.NodeCode, out var code));
            Assert.Null(code.CertificateSha256);

            // Nor is it offered as the panel's own certificate to download and trust.
            foreach (var path in new[] { "/api/v1/panel/certificate", "/api/v1/panel/certificate/download" })
            {
                using var offered = await admin.Http.GetAsync(new Uri(path, UriKind.Relative), Ct);
                await Auth.SetupTests.AssertProblemAsync(offered, HttpStatusCode.NotFound, PanelCertificateEndpoints.PubliclyTrustedCode);
            }
        }
        finally
        {
            store.Serve(null);
        }

        Assert.Equal(factory.ServerThumbprint, await PresentedAsync(factory.MainPort, "127.0.0.1"));
    }

    /// <summary>The certificate the server presents to a client that asks for <paramref name="host"/> (an IP address sends no SNI).</summary>
    private static async Task<string?> PresentedAsync(int port, string host)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var tls = new SslStream(tcp.GetStream());
        string? presented = null;
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    // Only looked at, never trusted: nothing is sent over this connection.
                    presented = certificate?.GetCertHashString();
                    return presented is not null;
                },
            }, Ct);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            // The agents' endpoint wants a client certificate: what it presented first is what counts.
        }

        return presented;
    }

    /// <summary>Over the installer's certificate, before the public one is served.</summary>
    private Task SetUpAdministratorAsync() => factory.OnceAsync("admin", async () =>
    {
        using var setup = await PanelClient.CreateAsync(factory.Handler(), factory.MainUrl);
        var token = factory.Services.GetRequiredService<InitialSetup>().IssueToken();
        using var response = await setup.PostAsync("/api/v1/auth/setup",
            new SetupRequest(token, PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword, "Administrator", null));
        await AccessScenario.EnsureSuccessAsync(response);
    });

    /// <summary>Signed in over the public certificate: its client accepts that one only.</summary>
    private async Task<PanelClient> AdminAsync(string publicThumbprint)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(), publicThumbprint, StringComparison.OrdinalIgnoreCase);
        var admin = await PanelClient.CreateAsync(handler, factory.MainUrl);
        using var login = await admin.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword);
        await AccessScenario.EnsureSuccessAsync(login);
        await admin.GetSessionAsync();
        return admin;
    }

    private static X509Certificate2 Leaf(ECDsa key)
    {
        var request = new CertificateRequest(new X500DistinguishedName(string.Empty), key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build(critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(160));
    }

    public sealed class Panel : KestrelPanelFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.PostConfigure<PublicIpCertificateOptions>(options =>
            {
                options.TreatLoopbackAsInternet = true;
                options.Run = false;
            }));
        }
    }
}
