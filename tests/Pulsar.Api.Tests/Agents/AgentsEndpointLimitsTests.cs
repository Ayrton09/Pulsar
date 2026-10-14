using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Panel;
using Pulsar.Api.Agents;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// What a connection to the agents' endpoint can cost the panel before it says who it is (docs/architecture/08, T31):
/// the chain of its certificate is built from what the handshake carried with the machine's trust, at the cost of a
/// chain without a policy, and connections at once are capped on that endpoint alone, those not signed in yet more
/// tightly, per address too, and for a while only.
/// </summary>
public sealed class AgentsEndpointLimitsTests
{
    private static readonly TimeSpan s_deadline = new AgentsOptions().SignInDeadline;

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
        var limit = Limit(max: 2, pending: 2, perAddress: 2, out _);
        var release = new TaskCompletionSource();
        var served = 0;
        var handler = limit.Wrap(async _ =>
        {
            Interlocked.Increment(ref served);
            await release.Task;
        });

        var first = handler(From("10.0.0.1"));
        var second = handler(From("10.0.0.2"));
        await handler(From("10.0.0.3")).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, served);
        Assert.Equal(2, limit.Open);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(0, limit.Open);
        await handler(From("10.0.0.3"));
        Assert.Equal(3, served);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1", false)]
    [InlineData("10.0.0.1", "::ffff:10.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", "10.0.0.1", false)]
    [InlineData("10.0.0.1", "10.0.0.2", true)]
    [InlineData("2001:db8:0:1::1", "2001:db8:0:1:ffff::2", false)]
    [InlineData("2001:db8:0:1::1", "2001:db8:0:2::1", true)]
    [InlineData("10.0.0.1", "::a00:1", true)]
    public async Task ConnectionsNotSignedInAreCappedPerAddress(string first, string second, bool accepted)
    {
        // IPv4 by address (mapped into IPv6 or not), IPv6 by /64: what one host holds at the least.
        var limit = Limit(max: 10, pending: 10, perAddress: 1, out _);
        var release = new TaskCompletionSource();
        var served = 0;
        var handler = limit.Wrap(async _ =>
        {
            Interlocked.Increment(ref served);
            await release.Task;
        });

        var held = handler(From(first));
        var other = handler(From(second));

        Assert.Equal(accepted ? 2 : 1, served);
        Assert.Equal(accepted ? 2 : 1, limit.Pending);
        release.SetResult();
        await Task.WhenAll(held, other);
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
    }

    [Fact]
    public async Task ConnectionsNotSignedInHaveABudgetOfTheirOwnWhichSignedInOnesLeave()
    {
        var limit = Limit(max: 3, pending: 2, perAddress: 2, out _);
        var release = new TaskCompletionSource();
        var signIn = new TaskCompletionSource();
        var served = 0;
        var handler = limit.Wrap(async connection =>
        {
            Interlocked.Increment(ref served);
            if (connection.Items.ContainsKey("agent"))
            {
                await signIn.Task;
                // What the agents' authentication does with each request that presents a valid session.
                connection.Features.Get<AgentsEndpointConnection>()!.Authenticated();
                connection.Features.Get<AgentsEndpointConnection>()!.Authenticated();
            }

            await release.Task;
        });

        var agent = From("10.0.0.1");
        agent.Items["agent"] = true;
        var connections = new List<Task> { handler(agent), handler(From("10.0.0.2")) };
        // The budget of connections not signed in is full; the endpoint is not.
        await handler(From("10.0.0.3"));
        Assert.Equal(2, served);
        Assert.Equal(2, limit.Pending);

        signIn.SetResult();
        Assert.Equal(1, limit.Pending);
        Assert.Equal(1, limit.Addresses);
        connections.Add(handler(From("10.0.0.1")));
        Assert.Equal(3, served);
        // The endpoint is full now: MaxConnections stays the outer bound.
        await handler(From("10.0.0.4"));
        Assert.Equal(3, served);
        Assert.Equal(3, limit.Open);

        release.SetResult();
        await Task.WhenAll(connections);
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
    }

    [Fact]
    public async Task ACountedConnectionIsReleasedWhenItFails()
    {
        var limit = Limit(max: 2, pending: 2, perAddress: 2, out _);
        var handler = limit.Wrap(_ => throw new InvalidOperationException("the connection broke"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(From("10.0.0.1")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(From("2001:db8::1")));

        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
    }

    [Fact]
    public async Task AConnectionThatDoesNotSignInIsClosedAtTheDeadline()
    {
        var limit = Limit(max: 10, pending: 10, perAddress: 10, out var clock);
        var release = new TaskCompletionSource();
        var handler = limit.Wrap(async connection =>
        {
            if (connection.Items.ContainsKey("agent"))
            {
                connection.Features.Get<AgentsEndpointConnection>()!.Authenticated();
            }

            await release.Task;
        });
        var idle = From("10.0.0.1");
        var idleLifetime = new Lifetime();
        idle.Features.Set<IConnectionLifetimeNotificationFeature>(idleLifetime);
        var agent = From("10.0.0.1");
        var agentLifetime = new Lifetime();
        agent.Features.Set<IConnectionLifetimeNotificationFeature>(agentLifetime);
        agent.Items["agent"] = true;
        var connections = Task.WhenAll(handler(idle), handler(agent));

        clock.Advance(s_deadline - TimeSpan.FromSeconds(1));
        Assert.False(idleLifetime.CloseRequested);

        // First as Kestrel closes an idle keep-alive connection: once the request in progress, if any, is answered.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(idleLifetime.CloseRequested);
        Assert.False(idle.ConnectionClosed.IsCancellationRequested);

        // Then whatever it is doing.
        clock.Advance(AgentsConnectionLimit.CloseGrace);
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(idle.ConnectionClosed.IsCancellationRequested), "the idle connection aborted");

        // A signed-in connection is never timed out.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(agentLifetime.CloseRequested);
        Assert.False(agent.ConnectionClosed.IsCancellationRequested);
        Assert.Equal(1, limit.Pending);

        release.SetResult();
        await connections;
        Assert.Equal(0, limit.Open);
        Assert.Equal(0, limit.Pending);
        Assert.Equal(0, limit.Addresses);
    }

    [Fact]
    public async Task AConnectionThatSignsInDuringItsGraceIsNotAborted()
    {
        var limit = Limit(max: 10, pending: 10, perAddress: 10, out var clock);
        var release = new TaskCompletionSource();
        var handler = limit.Wrap(_ => release.Task);
        var connection = From("10.0.0.1");
        var lifetime = new Lifetime();
        connection.Features.Set<IConnectionLifetimeNotificationFeature>(lifetime);
        var served = handler(connection);

        clock.Advance(s_deadline);
        Assert.True(lifetime.CloseRequested);
        // A request on its way when the deadline came presented a session: it is answered, then the connection closes.
        connection.Features.Get<AgentsEndpointConnection>()!.Authenticated();
        clock.Advance(AgentsConnectionLimit.CloseGrace + TimeSpan.FromMinutes(1));
        Assert.False(connection.ConnectionClosed.IsCancellationRequested);

        release.SetResult();
        await served;
        Assert.Equal(0, limit.Open);
    }

    [Fact]
    public async Task RefusalsAndTimeoutsAreLoggedAtMostOnceAMinute()
    {
        var logger = new CountingLogger();
        var clock = new FakeTimeProvider();
        var limit = new AgentsConnectionLimit(new AgentsOptions { MaxConnections = 1, MaxPendingConnections = 1 }, logger, clock);
        var release = new TaskCompletionSource();
        var handler = limit.Wrap(_ => release.Task);
        var held = handler(From("10.0.0.1"));

        for (var i = 0; i < 5; i++)
        {
            await handler(From("10.0.0.2"));
        }

        Assert.Single(logger.Messages);
        clock.Advance(s_deadline);
        await handler(From("10.0.0.2"));
        Assert.Single(logger.Messages);

        // The held connection timed out meanwhile: the next entry has it and the refusals since the first.
        clock.Advance(TimeSpan.FromMinutes(1) - s_deadline);
        await handler(From("10.0.0.2"));
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("closed 6 connection(s)", logger.Messages[1], StringComparison.Ordinal);
        Assert.Contains("and 1 that", logger.Messages[1], StringComparison.Ordinal);

        release.SetResult();
        await held;
    }

    [Theory]
    [InlineData("0", null, null)]
    [InlineData(null, "0", null)]
    [InlineData(null, null, "0")]
    [InlineData("10", "11", null)]
    [InlineData(null, "8", "9")]
    public void BudgetsAreCheckedAtStartup(string? max, string? pending, string? perAddress)
    {
        using var services = Services(new Dictionary<string, string?>
        {
            ["Agents:MaxConnections"] = max,
            ["Agents:MaxPendingConnections"] = pending,
            ["Agents:MaxPendingConnectionsPerAddress"] = perAddress,
        });

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<AgentsOptions>>().Value);
    }

    [Fact]
    public void BudgetsNotSetFollowTheEndpointsCap()
    {
        using (var services = Services([]))
        {
            var options = services.GetRequiredService<IOptions<AgentsOptions>>().Value;
            Assert.Equal(1000, options.MaxConnections);
            Assert.Equal(250, options.PendingLimit);
            Assert.Equal(20, options.PendingPerAddressLimit);
        }

        using (var services = Services(new Dictionary<string, string?> { ["Agents:MaxConnections"] = "40" }))
        {
            var options = services.GetRequiredService<IOptions<AgentsOptions>>().Value;
            Assert.Equal(10, options.PendingLimit);
            Assert.Equal(10, options.PendingPerAddressLimit);
        }
    }

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddPulsarApi(configuration).BuildServiceProvider();
    }

    private static AgentsConnectionLimit Limit(int max, int pending, int perAddress, out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider();
        return new AgentsConnectionLimit(
            new AgentsOptions { MaxConnections = max, MaxPendingConnections = pending, MaxPendingConnectionsPerAddress = perAddress },
            NullLogger.Instance, clock);
    }

    private static DefaultConnectionContext From(string address) =>
        new() { RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), 50000) };

    private sealed class Lifetime : IConnectionLifetimeNotificationFeature
    {
        public CancellationToken ConnectionClosedRequested { get; set; }

        public bool CloseRequested { get; private set; }

        public void RequestClose() => CloseRequested = true;
    }

    private sealed class CountingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
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
    internal static async Task<int> ReadAsync(NetworkStream stream)
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
            builder.UseSetting("Agents:MaxPendingConnections", "2");
        }
    }
}

/// <summary>
/// The agents' endpoint on real Kestrel with room for two connections not signed in, which must sign in within a second.
/// </summary>
public sealed class AgentsEndpointSignInDeadlineTests(AgentsEndpointSignInDeadlineTests.ShortDeadlinePanel factory)
    : IClassFixture<AgentsEndpointSignInDeadlineTests.ShortDeadlinePanel>
{
    private static readonly TimeSpan s_deadline = TimeSpan.FromSeconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AConnectionIdleAfterItsHandshakeIsClosedAtTheDeadline()
    {
        factory.StartServer();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = KestrelPanelFactory.UsableCertificate(key);
        using var tcp = new TcpClient();
        var watch = Stopwatch.StartNew();
        await tcp.ConnectAsync(new IPEndPoint(IPAddress.Loopback, factory.AgentsPort), Ct);
        await using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = (_, server, _, _) => factory.IsPanel(server),
            ClientCertificates = [certificate],
            LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
        }, Ct);

        // Closed as an idle keep-alive connection is, at the deadline: not after the grace a request in progress gets.
        using var wait = new CancellationTokenSource(s_deadline + (AgentsConnectionLimit.CloseGrace / 2));
        int read;
        try
        {
            read = await tls.ReadAsync(new byte[16], wait.Token);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.Equal(0, read);
        Assert.True(watch.Elapsed >= s_deadline - TimeSpan.FromMilliseconds(100), $"closed after {watch.Elapsed}");
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Open == 0 && factory.Limit.Pending == 0 && factory.Limit.Addresses == 0),
            "the endpoint's counters back to zero");
    }

    [DatabaseFact]
    public async Task AnAgentsHubOutlivesTheDeadlineAndLeavesTheBudgetOfConnectionsNotSignedIn()
    {
        factory.StartServer();
        var created = await factory.CreateNodeAsync("deadline-agent");
        await using (var agent = new InProcessAgent(factory, transport: new TrustingTransport(factory)))
        {
            var config = await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint, factory.MainUrl.ToString());
            await agent.StartAsync();
            await InProcessAgent.EventuallyAsync(() => Task.FromResult(agent.CurrentPanel == factory.AgentsUrl && agent.CurrentSessionId is not null),
                "the agent on the agents' address");
            var session = agent.CurrentSessionId;

            await Task.Delay(s_deadline + AgentsConnectionLimit.CloseGrace + TimeSpan.FromSeconds(1), Ct);

            // The same session on the same hub connection: it was never closed.
            Assert.Equal(session, agent.CurrentSessionId);
            var ping = await factory.Services.GetRequiredService<IAgentConnections>().SendAsync(new NodeId(config.NodeId), new PingCommand("after the deadline"), null, Ct);
            Assert.Equal(AckStatus.Accepted, ping?.Status);
            Assert.Equal(1, factory.Limit.Open);
            Assert.Equal(0, factory.Limit.Pending);

            // The two places for connections not signed in are free: the hub does not hold one.
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var certificate = KestrelPanelFactory.UsableCertificate(key);
            using (var http = new HttpClient(factory.Handler(certificate)) { BaseAddress = factory.AgentsUrl })
            {
                using var response = await http.GetAsync(AgentProtocol.PanelIdentityPath.TrimStart('/'), Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var agents = new IPEndPoint(IPAddress.Loopback, factory.AgentsPort);
            using var first = new TcpClient();
            using var second = new TcpClient();
            await first.ConnectAsync(agents, Ct);
            await second.ConnectAsync(agents, Ct);
            await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Pending == 2), "both idle connections counted");
            using var refused = new TcpClient();
            await refused.ConnectAsync(agents, Ct);
            Assert.Equal(0, await AgentsEndpointConnectionLimitTests.ReadAsync(refused.GetStream()));
        }

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Limit.Open == 0 && factory.Limit.Pending == 0 && factory.Limit.Addresses == 0),
            "the endpoint's counters back to zero");
    }

    /// <summary>The agent's real transport (sockets, TLS, WebSockets), trusting the test panel's certificate.</summary>
    private sealed class TrustingTransport(KestrelPanelFactory panel) : PanelTransport
    {
        public override HttpMessageHandler CreateHttpHandler() => panel.Handler();
    }

    public sealed class ShortDeadlinePanel : KestrelPanelFactory
    {
        internal AgentsConnectionLimit Limit => Services.GetRequiredService<AgentsEndpoint>().ConnectionLimit!;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Agents:MaxPendingConnections", "2");
            builder.ConfigureTestServices(services => services.Configure<AgentsOptions>(o => o.SignInDeadline = s_deadline));
        }
    }
}
