using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pulsar.Api.Certificates;

namespace Pulsar.Api.Tests.Certificates;

/// <summary>
/// The http-01 responder on its own port: the key authorization of a pending token for exactly
/// <c>GET /.well-known/acme-challenge/&lt;token&gt;</c>, 404 with nothing for everything else, and its bounds.
/// </summary>
public sealed class AcmeChallengeListenerTests : IAsyncLifetime
{
    private const string Token = "LoqXcYV8q5ONbJQxbmR7SCTNo3tiAXDfowyjxAjEuX0";
    private const string KeyAuthorization = Token + ".9jg46WB3rR_AHD-EBXdN7cBkH1WOu0tA3M9fm21mqTI";
    private readonly AcmeChallenges _challenges = new();
    private AcmeChallengeListener _listener = null!;
    private IDisposable _pending = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _listener = new AcmeChallengeListener(
            Options.Create(new PublicIpCertificateOptions { ChallengeBindAddress = "127.0.0.1", ChallengePortOverride = 0, MaxChallengeConnectionsPerAddress = 2 }),
            _challenges, NullLoggerFactory.Instance, NullLogger<AcmeChallengeListener>.Instance);
        Assert.True(await _listener.EnsureStartedAsync(Ct), _listener.Error);
        _pending = _challenges.Add(Token, KeyAuthorization);
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Dispose();
        await _listener.DisposeAsync();
    }

    [Fact]
    public async Task APendingTokenGetsItsKeyAuthorizationAsPlainText()
    {
        var (status, headers, body) = await RawAsync($"GET /.well-known/acme-challenge/{Token} HTTP/1.1\r\nHost: 1.2.3.4\r\nConnection: close\r\n\r\n");

        Assert.Equal(200, status);
        Assert.Equal(KeyAuthorization, body);
        Assert.Contains("Content-Type: text/plain", headers, StringComparison.Ordinal);
        Assert.Contains("Cache-Control: no-store", headers, StringComparison.Ordinal);
        Assert.Contains("X-Content-Type-Options: nosniff", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("Server:", headers, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1")]
    [InlineData("GET /index.html HTTP/1.1")]
    [InlineData("GET /.well-known/acme-challenge/ HTTP/1.1")]
    [InlineData("GET /.well-known/acme-challenge HTTP/1.1")]
    [InlineData("GET /.well-known/acme-challenge/AnotherTokenThatIsNotPendingAtAllxxxxxxxxxx HTTP/1.1")]
    [InlineData("GET /.well-known/acme-challenge/" + Token + "/ HTTP/1.1")]
    [InlineData("GET /.well-known/acme-challenge/" + Token + "?x=1 HTTP/1.1")]
    [InlineData("GET /.well-known/acme-challenge/%4CoqXcYV8q5ONbJQxbmR7SCTNo3tiAXDfowyjxAjEuX0 HTTP/1.1")]
    [InlineData("GET /.well-known/acme-challenge/../acme-challenge/" + Token + " HTTP/1.1")]
    [InlineData("GET //.well-known/acme-challenge/" + Token + " HTTP/1.1")]
    [InlineData("GET http://1.2.3.4/.well-known/acme-challenge/" + Token + " HTTP/1.1")]
    [InlineData("POST /.well-known/acme-challenge/" + Token + " HTTP/1.1")]
    [InlineData("PUT /.well-known/acme-challenge/" + Token + " HTTP/1.1")]
    [InlineData("DELETE /.well-known/acme-challenge/" + Token + " HTTP/1.1")]
    [InlineData("OPTIONS * HTTP/1.1")]
    public async Task EverythingElseIsAnEmpty404(string requestLine)
    {
        var (status, headers, body) = await RawAsync($"{requestLine}\r\nHost: 1.2.3.4\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        Assert.Equal(404, status);
        Assert.Empty(body);
        Assert.DoesNotContain("Location:", headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadGetsNothingEither()
    {
        var (status, _, body) = await RawAsync($"HEAD /.well-known/acme-challenge/{Token} HTTP/1.1\r\nHost: 1.2.3.4\r\nConnection: close\r\n\r\n");

        Assert.Equal(404, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task ATokenNoLongerPendingIs404()
    {
        using (_challenges.Add("SecondTokenSecondTokenSecondTokenSecond01", "x.y"))
        {
            Assert.Equal(200, (await RawAsync("GET /.well-known/acme-challenge/SecondTokenSecondTokenSecondTokenSecond01 HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n")).Status);
        }

        Assert.Equal(404, (await RawAsync("GET /.well-known/acme-challenge/SecondTokenSecondTokenSecondTokenSecond01 HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n")).Status);
    }

    [Fact]
    public async Task OversizedRequestsAndBodiesAreRefused()
    {
        var headers = await RawAsync($"GET /.well-known/acme-challenge/{Token} HTTP/1.1\r\nHost: a\r\nX-Filler: {new string('a', 8000)}\r\nConnection: close\r\n\r\n");
        Assert.Equal(431, headers.Status);
        var line = await RawAsync($"GET /.well-known/acme-challenge/{Token}{new string('a', 2000)} HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n");
        Assert.Equal(414, line.Status);
        var body = await RawAsync($"GET /.well-known/acme-challenge/{Token} HTTP/1.1\r\nHost: a\r\nContent-Length: 10\r\nConnection: close\r\n\r\n0123456789");
        Assert.True(body.Status is 404 or 413, $"{body.Status}");
    }

    [Fact]
    public async Task OneAddressHoldsAFewConnectionsAtMost()
    {
        TcpClient?[] clients = [await TryConnectAsync(), await TryConnectAsync(), await TryConnectAsync()];
        try
        {
            // One of the three (whichever the server took last) is closed at once, before any request; the others stay
            // open (until the 2-second idle limit) and answer.
            var closed = await Task.WhenAll(clients.Select(c => c is null ? Task.FromResult(true) : ClosedWithinAsync(c, TimeSpan.FromSeconds(1))));
            Assert.Equal(1, closed.Count(c => c));
            var open = clients[Array.IndexOf(closed, false)]!;
            await open.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /.well-known/acme-challenge/{Token} HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n"), Ct);
            Assert.StartsWith("HTTP/1.1 200", await new StreamReader(open.GetStream()).ReadToEndAsync(Ct), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var client in clients)
            {
                client?.Dispose();
            }
        }
    }

    [Fact]
    public async Task WithNoTokenPendingEveryConnectionIsClosedOnArrival()
    {
        _pending.Dispose();
        TcpClient?[] clients = [await TryConnectAsync(), await TryConnectAsync()];
        try
        {
            Assert.All(await Task.WhenAll(clients.Select(c => c is null ? Task.FromResult(true) : ClosedWithinAsync(c, TimeSpan.FromSeconds(1)))),
                closed => Assert.True(closed));
        }
        finally
        {
            foreach (var client in clients)
            {
                client?.Dispose();
            }
        }

        _pending = _challenges.Add(Token, KeyAuthorization);
    }

    [Fact]
    public async Task AConnectionThatSendsNothingIsClosedAfterTwoSeconds()
    {
        using var idle = await ConnectAsync();

        // Kestrel looks at its timeouts once a second: the 2-second limit closes it within four, or a little more on a
        // busy machine (a CI runner); a connection left open would not be closed at all.
        Assert.True(await ClosedWithinAsync(idle, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AddressesAlreadyHoldingConnectionsLeaveRoomForOthersOnceHalfIsHeld()
    {
        var budget = new AcmeChallengeListener.ConnectionBudget(perAddress: 4, total: 8);

        // One address takes all its four (half of the eight): any of its further ones is refused ...
        Assert.All(Enumerable.Range(0, 4), _ => Assert.True(budget.TryTake("a")));
        Assert.False(budget.TryTake("a"));
        // ... and four others still get one each (Let's Encrypt asks from several places), but not a second.
        Assert.True(budget.TryTake("b"));
        Assert.False(budget.TryTake("b"));
        Assert.True(budget.TryTake("c"));
        Assert.True(budget.TryTake("d"));
        Assert.True(budget.TryTake("e"));
        Assert.False(budget.TryTake("f"));

        budget.Return("a");
        Assert.True(budget.TryTake("f"));
    }

    [Fact]
    public void NoOtherSocketOnThisMachineCanTakeItsPort()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Exclusive binding is Windows' (SO_EXCLUSIVEADDRUSE).");
        using var squatter = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        squatter.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        Assert.Throws<SocketException>(() => squatter.Bind(new IPEndPoint(IPAddress.Loopback, _listener.ListeningPort!.Value)));
    }

    [Fact]
    public async Task StoppedItListensNoMore()
    {
        var port = _listener.ListeningPort!.Value;

        await _listener.StopAsync();

        Assert.Null(_listener.ListeningPort);
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port, Ct).AsTask());
    }

    [Fact]
    public async Task APortThatIsTakenIsReportedNotThrown()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        await using var listener = new AcmeChallengeListener(
            Options.Create(new PublicIpCertificateOptions { ChallengeBindAddress = "127.0.0.1", ChallengePortOverride = port }),
            _challenges, NullLoggerFactory.Instance, NullLogger<AcmeChallengeListener>.Instance);

        Assert.False(await listener.EnsureStartedAsync(Ct));

        Assert.Null(listener.ListeningPort);
        Assert.StartsWith($"Port {port} could not be opened", listener.Error, StringComparison.Ordinal);
    }

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _listener.ListeningPort!.Value, Ct);
        return client;
    }

    /// <summary>
    /// A connection, or null when the server reset it as it was made: one it does not keep is closed at once, and on Linux
    /// that reset can reach the client before its connect completes.
    /// </summary>
    private async Task<TcpClient?> TryConnectAsync()
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, _listener.ListeningPort!.Value, Ct);
            return client;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
        {
            client.Dispose();
            return null;
        }
    }

    /// <summary>The server closed the connection within <paramref name="wait"/> (an end of stream or a reset).</summary>
    private static async Task<bool> ClosedWithinAsync(TcpClient client, TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(wait);
        try
        {
            return await client.GetStream().ReadAsync(new byte[16], timeout.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<(int Status, string Headers, string Body)> RawAsync(string request)
    {
        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        var text = await new StreamReader(stream, Encoding.ASCII).ReadToEndAsync(Ct);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var status = int.Parse(head.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        return (status, head, split < 0 ? string.Empty : text[(split + 4)..]);
    }
}
