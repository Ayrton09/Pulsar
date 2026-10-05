using System.Net;
using System.Net.Sockets;
using System.Text;
using Pulsar.Agent.Core.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The telnet console against a fake 7 Days to Die: a banner, a password prompt and its log streamed all the time.</summary>
public sealed class TelnetConsoleTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
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

        await using var session = await new TelnetConsoleConnector().ConnectAsync(endpoint, TestContext.Current.CancellationToken);
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

        await using var session = await new TelnetConsoleConnector().ConnectAsync(endpoint, TestContext.Current.CancellationToken);

        Assert.Equal("Total of 0 in the game", await session.ExecuteAsync("lp", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWrongPasswordIsRefused()
    {
        var endpoint = Serve(password: "s3cret") with { Password = "wrong" };

        var error = await Assert.ThrowsAsync<RconException>(() => new TelnetConsoleConnector().ConnectAsync(endpoint, TestContext.Current.CancellationToken));

        Assert.True(error.AuthenticationFailed);
    }

    [Fact]
    public async Task NothingListeningIsAConnectionError() =>
        await Assert.ThrowsAsync<RconException>(() => new TelnetConsoleConnector { ConnectTimeout = TimeSpan.FromSeconds(1) }
            .ConnectAsync(new RconEndpoint(RconProtocol.Telnet, "127.0.0.1", 1, "x"), TestContext.Current.CancellationToken));

    [Fact]
    public void AnAnswerLeavesOutLogLinesAndBlankLines() =>
        Assert.Equal("Game version: V 3.2.0\nMod TFP_Harmony: 1.1.0.4", TelnetConsoleSession.Answer(
            "2026-10-05T12:14:40 71.457 INF Executing command 'version' by Telnet from 127.0.0.1:62479\r\n" +
            "Game version: V 3.2.0\r\n\r\nMod TFP_Harmony: 1.1.0.4\r\n2026-10-05T12:14:41 72.001 WRN something else\r\n"));

    private RconEndpoint Serve(string? password)
    {
        _listener.Start();
        _server = Task.Run(() => RunAsync(password, _stop.Token));
        return new RconEndpoint(RconProtocol.Telnet, "127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port, password ?? "unused");
    }

    private async Task RunAsync(string? password, CancellationToken cancellationToken)
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
            if (await reader.ReadLineAsync(cancellationToken) != password)
            {
                await WriteAsync("Password incorrect, please enter password:\r\n");
                return;
            }

            await WriteAsync("Logon successful.\r\n\r\n");
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
