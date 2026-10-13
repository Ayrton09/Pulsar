using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pulsar.Agent.Core.Panel;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// How the agent checks the panel's TLS certificate (docs/architecture/03, "Transport"): its chain is built from the
/// machine's trusted roots and what the handshake brought, never from what the certificate says to fetch. The agent runs
/// as SYSTEM, and whoever answers at the panel's address (a DNS answer, an intercepted route) chooses those URLs.
/// </summary>
public sealed class PanelCertificateChainTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TcpListener _issuerSite = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<string> _fetched = new();
    private readonly Task _serving;
    private readonly string _site;
    private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;
    private readonly X509Certificate2 _leaf;

    public PanelCertificateChainTests()
    {
        // Answers the URLs the certificates name with the intermediate's certificate, as a real issuer's site would.
        _issuerSite.Start();
        _site = $"http://127.0.0.1:{((IPEndPoint)_issuerSite.LocalEndpoint).Port}/{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        // Names of their own: Windows remembers the certificates of earlier chains in this process, found by name.
        var names = Guid.NewGuid().ToString("N");
        var rootRequest = new CertificateRequest($"CN=Pulsar test root {names}", _rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _root = rootRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(30));
        var intermediateRequest = new CertificateRequest($"CN=Pulsar test intermediate {names}", _intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension([$"{_site}/root-ocsp"], [$"{_site}/root.cer"]));
        _intermediate = intermediateRequest.Create(_root, now.AddDays(-1), now.AddDays(20), RandomNumberGenerator.GetBytes(12));
        var leafRequest = new CertificateRequest("CN=localhost", _leafKey, HashAlgorithmName.SHA256);
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName("localhost");
        leafRequest.CertificateExtensions.Add(alternativeNames.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        leafRequest.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension([$"{_site}/ocsp"], [$"{_site}/intermediate.cer"]));
        leafRequest.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([$"{_site}/intermediate.crl"]));
        using var leaf = leafRequest.Create(_intermediate.CopyWithPrivateKey(_intermediateKey), now.AddDays(-1), now.AddDays(10), RandomNumberGenerator.GetBytes(12));
        using var withKey = leaf.CopyWithPrivateKey(_leafKey);
        _leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "test"), "test");
        _serving = ServeIssuerAsync();
    }

    [Fact]
    public async Task AnIntermediateThePanelSendsCompletesTheChain()
    {
        await using var panel = new TlsPanel(SslStreamCertificateContext.Create(_leaf, [_intermediate, _root], offline: true));
        var transport = new ObservingTransport();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(transport, panel.Port));

        // Leaf, intermediate and root, all from the handshake, the root's trust being the only thing missing: on a real
        // panel the machine's store holds it (and the panel need not send it).
        var chain = Assert.Single(transport.Chains);
        Assert.Equal([_leaf.Thumbprint, _intermediate.Thumbprint, _root.Thumbprint], chain.Elements);
        Assert.Contains(X509ChainStatusFlags.UntrustedRoot, chain.Status);
        Assert.DoesNotContain(X509ChainStatusFlags.PartialChain, chain.Status);
        await AssertNothingFetchedAsync();
    }

    [Fact]
    public async Task AnIntermediateThePanelDoesNotSendIsNeverFetched()
    {
        await using var panel = new TlsPanel(SslStreamCertificateContext.Create(_leaf, [], offline: true));
        var transport = new ObservingTransport();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(transport, panel.Port));

        var chain = Assert.Single(transport.Chains);
        Assert.Equal([_leaf.Thumbprint], chain.Elements);
        Assert.Contains(X509ChainStatusFlags.PartialChain, chain.Status);
        await AssertNothingFetchedAsync();
    }

    [Fact]
    public async Task TheHubsWebSocketChecksThePanelTheSameWay()
    {
        await using var panel = new TlsPanel(SslStreamCertificateContext.Create(_leaf, [], offline: true));
        var transport = new ObservingTransport();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            (await transport.ConnectHubAsync(new Uri($"wss://localhost:{panel.Port}/agent/v1/hub"), "token", null, Ct)).Dispose());

        var chain = Assert.Single(transport.Chains);
        Assert.Equal([_leaf.Thumbprint], chain.Elements);
        Assert.Contains(X509ChainStatusFlags.PartialChain, chain.Status);
        await AssertNothingFetchedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _issuerSite.Stop();
        await Record.ExceptionAsync(() => _serving);
        _issuerSite.Dispose();
        _leaf.Dispose();
        _intermediate.Dispose();
        _root.Dispose();
        _leafKey.Dispose();
        _intermediateKey.Dispose();
        _rootKey.Dispose();
    }

    private static async Task GetAsync(PanelTransport transport, int port)
    {
        using var http = new HttpClient(transport.CreateHttpHandler());
        using var response = await http.GetAsync(new Uri($"https://localhost:{port}/agent/v1/panel-identity"), Ct);
    }

    private async Task AssertNothingFetchedAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        Assert.Empty(_fetched);
    }

    /// <summary>The issuer's site: records what this process asks it for, and answers with the intermediate.</summary>
    private async Task ServeIssuerAsync()
    {
        while (true)
        {
            using var client = await _issuerSite.AcceptTcpClientAsync();
            if (TcpOwners.ProcessOf((IPEndPoint)client.Client.RemoteEndPoint!) is { } owner && owner != Environment.ProcessId)
            {
                continue;
            }

            var stream = client.GetStream();
            var buffer = new byte[1024];
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var read = await stream.ReadAsync(buffer, wait.Token).AsTask().ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : 0);
            _fetched.Enqueue(Encoding.ASCII.GetString(buffer, 0, read).Split('\r')[0]);
            var body = _intermediate.RawData;
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-cert\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }
    }

    /// <summary>The agent's real transport, recording the chain it built for the panel's certificate.</summary>
    private sealed class ObservingTransport : PanelTransport
    {
        public ConcurrentQueue<(string[] Elements, X509ChainStatusFlags[] Status)> Chains { get; } = new();

        public override HttpMessageHandler CreateHttpHandler()
        {
            var handler = (SocketsHttpHandler)base.CreateHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, chain, _) =>
            {
                Chains.Enqueue(([.. chain!.ChainElements.Select(e => e.Certificate.Thumbprint)], [.. chain.ChainStatus.Select(s => s.Status)]));
                return false;
            };
            return handler;
        }
    }

    /// <summary>A TLS server presenting the panel's certificate (and what <c>context</c> adds), refused by the client.</summary>
    private sealed class TlsPanel : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _accepting;

        public TlsPanel(SslStreamCertificateContext context)
        {
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        await using (var tls = new SslStream(client.GetStream()))
                        {
                            await Record.ExceptionAsync(() => tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context }));
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await Record.ExceptionAsync(() => _accepting);
            _listener.Dispose();
        }
    }
}
