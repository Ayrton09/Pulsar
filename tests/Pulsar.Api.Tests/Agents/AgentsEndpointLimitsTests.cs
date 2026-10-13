using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Api.Agents;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// What a connection to the agents' endpoint can cost the panel before it says who it is (docs/architecture/08, T31):
/// the chain of its certificate is built from what the handshake carried with the machine's trust, at the cost of a
/// chain without a policy, and connections at once are capped on that endpoint alone.
/// </summary>
public sealed class AgentsEndpointLimitsTests
{
    [Fact]
    public void AClientCertificatesChainIsBuiltWithoutFetchingAnythingAndWithoutACustomTrustEngine()
    {
        var policy = AgentsEndpoint.ClientChainPolicy();

        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        // A custom root trust builds a chain engine of its own for every handshake: about 25 times the work, for a
        // chain whose result is ignored anyway.
        Assert.Equal(X509ChainTrustMode.System, policy.TrustMode);
    }

    [Fact]
    public async Task ConnectionsOverTheLimitAreClosedBeforeAnythingElse()
    {
        var limit = new AgentsConnectionLimit(2, NullLogger.Instance, new FakeTimeProvider());
        var release = new TaskCompletionSource();
        var served = 0;
        var handler = limit.Wrap(async _ =>
        {
            Interlocked.Increment(ref served);
            await release.Task;
        });

        var first = handler(new DefaultConnectionContext());
        var second = handler(new DefaultConnectionContext());
        await handler(new DefaultConnectionContext()).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, served);
        Assert.Equal(2, limit.Open);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(0, limit.Open);
        await handler(new DefaultConnectionContext());
        Assert.Equal(3, served);
    }
}

/// <summary>The agents' endpoint on real Kestrel with a limit of two connections at once.</summary>
public sealed class AgentsEndpointConnectionLimitTests(AgentsEndpointConnectionLimitTests.TwoConnectionsPanel factory)
    : IClassFixture<AgentsEndpointConnectionLimitTests.TwoConnectionsPanel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ATlsHandshakeWaitsForAConnectionToEnd()
    {
        factory.StartServer();
        var agents = new IPEndPoint(IPAddress.Loopback, factory.AgentsPort);

        // Two connections that never start their handshake hold the endpoint; the third is closed at once, unread.
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(agents, Ct);
        await second.ConnectAsync(agents, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        using (var third = new TcpClient())
        {
            await third.ConnectAsync(agents, Ct);
            var read = await ReadAsync(third.GetStream());
            Assert.Equal(0, read);
        }

        // The main endpoint is not counted.
        using (var main = new HttpClient(factory.Handler()) { BaseAddress = factory.MainUrl })
        {
            Assert.Equal(HttpStatusCode.OK, (await main.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct)).StatusCode);
        }

        first.Dispose();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = KestrelPanelFactory.UsableCertificate(key);
        using var http = new HttpClient(factory.Handler(certificate)) { BaseAddress = factory.AgentsUrl };
        await InProcessAgent.EventuallyAsync(async () =>
        {
            try
            {
                using var response = await http.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct);
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }, "a connection once one ended");
    }

    /// <summary>What the panel sends on a connection within two seconds: 0 when it closed it, -1 when it is still open.</summary>
    private static async Task<int> ReadAsync(NetworkStream stream)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            return await stream.ReadAsync(new byte[16], wait.Token);
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public sealed class TwoConnectionsPanel : KestrelPanelFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Agents:MaxConnections", "2");
        }
    }
}
