using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pulsar.Agent.Core.Panel;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// The panel certificate a node code pins (Phase 18, docs/architecture/03 "Transport"): with a pin the agent accepts the
/// panel's self-signed certificate without the machine trusting it, and nothing else; without one, the machine's trust
/// decides, as before. The pin never touches a certificate store.
/// </summary>
public sealed class PanelCertificatePinTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly X509Certificate2 _panel = SelfSigned("localhost");
    private readonly X509Certificate2 _other = SelfSigned("localhost");

    public void Dispose()
    {
        _panel.Dispose();
        _other.Dispose();
    }

    [Fact]
    public async Task ThePinnedCertificateIsAcceptedThoughNoRootTheMachineTrustsVouchesForIt()
    {
        await using var panel = new TlsPanel(_panel);

        Assert.Equal(HttpStatusCode.OK, await GetAsync(panel.Port, NodeCode.CertificateHash(_panel.RawData)));
        Assert.Equal(1, panel.Requests);
    }

    [Fact]
    public async Task AnyOtherCertificateIsRefusedWithAPin()
    {
        await using var panel = new TlsPanel(_other);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(panel.Port, NodeCode.CertificateHash(_panel.RawData)));
        Assert.Equal(0, panel.Requests);
    }

    [Fact]
    public async Task WithoutAPinTheMachinesTrustDecides()
    {
        await using var panel = new TlsPanel(_panel);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(panel.Port, null));
        Assert.Equal(0, panel.Requests);
    }

    [Fact]
    public async Task APinDoesNotExcuseACertificateForAnotherName()
    {
        using var elsewhere = SelfSigned("other.example");
        await using var panel = new TlsPanel(elsewhere);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(panel.Port, NodeCode.CertificateHash(elsewhere.RawData)));
        Assert.Equal(0, panel.Requests);
    }

    [Fact]
    public async Task TheHubsWebSocketIsPinnedTheSameWay()
    {
        await using var panel = new TlsPanel(_panel);
        var hub = new Uri($"wss://localhost:{panel.Port}/agent/v1/hub");

        // Not a WebSocket server: the upgrade fails either way, but only once the handshake passed is a request sent.
        await Assert.ThrowsAnyAsync<Exception>(async () => (await new PanelTransport().ConnectHubAsync(hub, "token", null, null, Ct)).Dispose());
        Assert.Equal(0, panel.Requests);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            (await new PanelTransport().ConnectHubAsync(hub, "token", null, NodeCode.CertificateHash(_other.RawData), Ct)).Dispose());
        Assert.Equal(0, panel.Requests);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            (await new PanelTransport().ConnectHubAsync(hub, "token", null, NodeCode.CertificateHash(_panel.RawData), Ct)).Dispose());
        Assert.Equal(1, panel.Requests);
    }

    [Fact]
    public async Task APinnedCertificateThatHasExpiredOrNotYetBegunIsRefused()
    {
        using var expired = SelfSigned("localhost", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        using var future = SelfSigned("localhost", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(10));

        foreach (var certificate in new[] { expired, future })
        {
            await using var panel = new TlsPanel(certificate);

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(panel.Port, NodeCode.CertificateHash(certificate.RawData)));
            Assert.Equal(0, panel.Requests);
        }
    }

    [Fact]
    public void APinnedAgentRefusesEvenACertificateTheMachineTrusts()
    {
        var pin = NodeCode.CertificateHash(_panel.RawData);
        using var chain = Build(_panel);
        using var otherChain = Build(_other);

        Assert.True(PanelTransport.IsPinnedCertificate(_panel, null, SslPolicyErrors.None, pin));
        Assert.True(PanelTransport.IsPinnedCertificate(_panel, chain, SslPolicyErrors.RemoteCertificateChainErrors, pin));
        Assert.False(PanelTransport.IsPinnedCertificate(_panel, null, SslPolicyErrors.RemoteCertificateChainErrors, pin));
        Assert.False(PanelTransport.IsPinnedCertificate(_other, otherChain, SslPolicyErrors.None, pin));
        Assert.False(PanelTransport.IsPinnedCertificate(_panel, chain, SslPolicyErrors.RemoteCertificateNameMismatch, pin));
        Assert.False(PanelTransport.IsPinnedCertificate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable, pin));
        Assert.False(PanelTransport.IsPinnedCertificate(_panel, chain, SslPolicyErrors.None, pin.ToUpperInvariant()));
    }

    [Fact]
    public void OnlyAMissingTrustedRootIsForgivenOfTheChainsErrors()
    {
        using var expired = SelfSigned("localhost", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        using var future = SelfSigned("localhost", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(10));

        foreach (var certificate in new[] { expired, future })
        {
            using var chain = Build(certificate);

            Assert.Contains(chain.ChainStatus, status => status.Status.HasFlag(X509ChainStatusFlags.NotTimeValid));
            Assert.False(PanelTransport.IsPinnedCertificate(certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors, NodeCode.CertificateHash(certificate.RawData)));
        }
    }

    /// <summary>The chain as the agent builds it: nothing fetched, no revocation check, the machine's roots.</summary>
    private static X509Chain Build(X509Certificate2 certificate)
    {
        var chain = new X509Chain { ChainPolicy = { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck } };
        chain.Build(certificate);
        return chain;
    }

    private static async Task<HttpStatusCode> GetAsync(int port, string? pin)
    {
        using var http = new PanelTransport().CreateClient(new Uri($"https://localhost:{port}/"), null, pin);
        using var response = await http.GetAsync(new Uri(AgentProtocol.PanelIdentityPath.TrimStart('/'), UriKind.Relative), Ct);
        return response.StatusCode;
    }

    /// <summary>A certificate like the installer's: self-signed, not an authority, for a web server's identity, for <paramref name="name"/>.</summary>
    private static X509Certificate2 SelfSigned(string name) => SelfSigned(name, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));

    private static X509Certificate2 SelfSigned(string name, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        // SChannel signs with a key it can open: an ephemeral one is not.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx, "test"), "test");
    }

    /// <summary>A TLS server presenting <c>certificate</c> that answers every request with 200 and counts them.</summary>
    private sealed class TlsPanel : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _accepting;
        private int _requests;

        public TlsPanel(X509Certificate2 certificate)
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
                            await Record.ExceptionAsync(() => AnswerAsync(tls, certificate));
                        }
                    });
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Requests => Volatile.Read(ref _requests);

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await Record.ExceptionAsync(() => _accepting);
            _listener.Dispose();
        }

        private async Task AnswerAsync(SslStream tls, X509Certificate2 certificate)
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
            var buffer = new byte[4096];
            var received = new StringBuilder();
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await tls.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            Interlocked.Increment(ref _requests);
            await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        }
    }
}
