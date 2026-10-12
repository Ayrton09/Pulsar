using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The panel on real Kestrel with real TLS, as installed: a main endpoint and the agents' endpoint (Phase 16) on two
/// loopback ports, both with a self-signed server certificate the tests trust by thumbprint.
/// </summary>
public sealed class KestrelPanelFactory : PulsarApiFactory
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pulsar-kestrel-").FullName;

    public KestrelPanelFactory()
    {
        MainPort = FreePort();
        AgentsPort = FreePort();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        ServerThumbprint = certificate.Thumbprint;
        CertificatePath = Path.Combine(_directory, "panel.pfx");
        File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx, CertificatePassword));
        UseKestrel();
    }

    public const string CertificatePassword = "kestrel-test";

    public int MainPort { get; }

    public int AgentsPort { get; }

    public string CertificatePath { get; }

    public string ServerThumbprint { get; }

    public Uri MainUrl => new($"https://127.0.0.1:{MainPort}/");

    public Uri AgentsUrl => new($"https://127.0.0.1:{AgentsPort}/");

    /// <summary>A handler that trusts this panel's certificate and presents <paramref name="clientCertificate"/>.</summary>
    public SocketsHttpHandler Handler(X509Certificate2? clientCertificate = null)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => IsPanel(certificate);
        if (clientCertificate is not null)
        {
            handler.SslOptions.ClientCertificates = [clientCertificate];
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate;
        }

        return handler;
    }

    public bool IsPanel(X509Certificate? certificate) =>
        certificate is not null && string.Equals(certificate.GetCertHashString(), ServerThumbprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A client certificate on <paramref name="key"/> whose private key SChannel can use: an in-memory key cannot
    /// sign a TLS handshake on Windows, a PFX loaded back into a key container can (the agent's CNG keys are persisted).
    /// </summary>
    public static X509Certificate2 UsableCertificate(ECDsa key)
    {
        using var certificate = TestAgent.CertificateFor(key);
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, CertificatePassword), CertificatePassword);
    }

    /// <summary>Creates a node as the system would (no browser session over this TLS), returning its enrollment.</summary>
    public async Task<CreatedNode> CreateNodeAsync(string name)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = new SystemContext();
        var created = await scope.ServiceProvider.GetRequiredService<NodeManagement>().CreateAsync(name, null, null, TestContext.Current.CancellationToken);
        return created.Value!;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (name, port) in new[] { ("Main", MainPort), ("Agents", AgentsPort) })
        {
            builder.UseSetting($"Kestrel:Endpoints:{name}:Url", $"https://127.0.0.1:{port}");
            builder.UseSetting($"Kestrel:Endpoints:{name}:Certificate:Path", CertificatePath);
            builder.UseSetting($"Kestrel:Endpoints:{name}:Certificate:Password", CertificatePassword);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Kestrel may still hold the certificate file for a moment; the temporary folder goes with the next cleanup.
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class SystemContext : IRequestContext
    {
        public UserId? UserId => null;

        public AuditActor Actor { get; } = new(AuditActorType.System, null, "kestrel tests");

        public AuditRequestInfo RequestInfo { get; } = new(null, null, null);
    }
}
