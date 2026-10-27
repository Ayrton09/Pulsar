using System.Net;
using System.Net.Sockets;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The telnet console against a fake 7 Days to Die: a banner, a password prompt and its log streamed all the time.</summary>
public sealed class TelnetConsoleTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _received = []; // what the game read after its password prompt
    private Task? _server;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_server is not null)
        {
            await _server.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        _stop.Dispose();
    }

    [Fact]
    public async Task ACommandGetsItsAnswerWithoutTheLogTheGameStreams()
    {
        var endpoint = Serve(password: "s3cret");

        await using var session = await new TelnetConsoleConnector().ConnectAsync(endpoint, ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken); // log lines pile up meanwhile
        var answer = await session.ExecuteAsync("lp", TestContext.Current.CancellationToken);
        var time = await session.ExecuteAsync("gettime", TestContext.Current.CancellationToken);

        Assert.Equal("Total of 0 in the game", answer);
        Assert.Equal("Day 1, 07:00", time);
    }

    [Fact]
    public async Task AGameWithoutAPasswordGreetsAtOnce()
    {
        var endpoint = Serve(password: null);

        await using var session = await new TelnetConsoleConnector().ConnectAsync(endpoint, ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        Assert.Equal("Total of 0 in the game", await session.ExecuteAsync("lp", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWrongPasswordIsRefused()
    {
        var endpoint = Serve(password: "s3cret") with { Password = "wrong" };

        var error = await Assert.ThrowsAsync<RconException>(() => new TelnetConsoleConnector().ConnectAsync(endpoint, ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken));

        Assert.True(error.AuthenticationFailed);
    }

    [Fact]
    public async Task ThePasswordGoesOnlyToAProcessRunningAsTheServersAccount()
    {
        var endpoint = Serve(password: "s3cret");
        var connector = new TelnetConsoleConnector { Peers = new FakePeers { Owner = ProcessIdentity.Account("pulsar-other") } };

        var error = await Assert.ThrowsAsync<RconException>(() => connector.ConnectAsync(endpoint, ProcessIdentity.Account("pulsar-abcd2345"),
            TestContext.Current.CancellationToken));

        Assert.True(error.NotTheGame);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task ACommandOnAConnectionTheGameClosedSaysItWasNotSent()
    {
        var endpoint = Serve(password: null);
        await using var session = await new TelnetConsoleConnector().ConnectAsync(endpoint, ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken);

        var quit = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("shutdown", TestContext.Current.CancellationToken));
        var next = await Assert.ThrowsAsync<RconException>(() => session.ExecuteAsync("lp", TestContext.Current.CancellationToken));

        Assert.True(quit.ConnectionLost);
        Assert.False(quit.NotSent); // the game got it
        Assert.True(next.ConnectionLost);
        Assert.True(next.NotSent);
    }

    [Fact]
    public async Task ATelnetConsoleThatNeverGreetsIsNotTakenForLoggedOn()
    {
        var endpoint = Serve(password: null, greets: false);

        var error = await Assert.ThrowsAsync<RconException>(() => new TelnetConsoleConnector { AnswerTimeout = TimeSpan.FromSeconds(1) }
            .ConnectAsync(endpoint, ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken));

        Assert.Contains("did not greet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingListeningIsAConnectionError() =>
        await Assert.ThrowsAsync<RconException>(() => new TelnetConsoleConnector { ConnectTimeout = TimeSpan.FromSeconds(1) }
            .ConnectAsync(new RconEndpoint(RconProtocol.Telnet, "127.0.0.1", 1, "x"), ProcessIdentity.CurrentUser, TestContext.Current.CancellationToken));

    [Fact]
    public void AnAnswerLeavesOutLogLinesAndBlankLines() =>
        Assert.Equal("Game version: V 3.2.0\nMod TFP_Harmony: 1.1.0.4", TelnetConsoleSession.Answer(
            "2026-10-05T12:14:40 71.457 INF Executing command 'version' by Telnet from 127.0.0.1:62479\r\n" +
            "Game version: V 3.2.0\r\n\r\nMod TFP_Harmony: 1.1.0.4\r\n2026-10-05T12:14:41 72.001 WRN something else\r\n"));

    private RconEndpoint Serve(string? password, bool greets = true)
    {
        _listener.Start();
        _server = Task.Run(() => RunAsync(password, greets, _stop.Token));
        return new RconEndpoint(RconProtocol.Telnet, "127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port, password ?? "unused");
    }

    private async Task RunAsync(string? password, bool greets, CancellationToken cancellationToken)
    {
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var writeLock = new SemaphoreSlim(1, 1);

        async Task WriteAsync(string text)
        {
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
            }
            finally
            {
                writeLock.Release();
            }
        }

        if (password is not null)
        {
            await WriteAsync("Please enter password:\r\n");
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is not null)
            {
                _received.Add(line);
            }

            if (line != password)
            {
                await WriteAsync("Password incorrect, please enter password:\r\n");
                return;
            }

            await WriteAsync("Logon successful.\r\n\r\n");
        }

        if (!greets)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } plain)
            {
                _received.Add(plain);
                await WriteAsync(plain == "lp" ? "Total of 0 in the game\r\n" : $"unknown command '{plain}'\r\n");
            }

            return;
        }

        await WriteAsync("*** Connected with 7DTD server.\r\n*** Server version: V 3.2.0 (b10)\r\n\r\nPress 'help' to get a list of all commands.\r\n\r\n");
        var tick = 0;
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WriteAsync($"2026-10-05T12:14:{tick % 60:00} {tick++}.000 INF [EOS] log line streamed to every client\r\n");
                await Task.Delay(30, cancellationToken);
            }
        }, cancellationToken);

        while (await reader.ReadLineAsync(cancellationToken) is { } command)
        {
            if (command == "shutdown")
            {
                return; // the game quits: the connection closes before any answer
            }

            await WriteAsync($"2026-10-05T12:14:43 74.506 INF Executing command '{command}' by Telnet from 127.0.0.1:62479\r\n");
            await WriteAsync(command switch
            {
                "lp" => "Total of 0 in the game\r\n",
                "gettime" => "Day 1, 07:00\r\n",
                _ => $"*** ERROR: unknown command '{command}'\r\n",
            });
        }
    }
}
