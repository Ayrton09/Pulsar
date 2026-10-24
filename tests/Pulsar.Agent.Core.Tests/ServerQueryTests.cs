using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

public sealed class ServerQueryTests
{
    private readonly ServerQuery _query = new(TimeProvider.System) { Timeout = TimeSpan.FromSeconds(15) }; // a busy runner can take seconds to answer on loopback

    [Fact]
    public async Task A2sGivesTheInfoAndThePlayersAfterTheirChallenges()
    {
        await using var game = new FakeA2sServer
        {
            Info = Info("Pulsar ‮!dlrow\u001b[31m", "de_dust2", players: 2, max: 16, bots: 1, "1.40.2.1"),
            Players = PlayersPayload(("Ana", 12, 61.7f), ("", 0, 1f), ("Bob\nby", -3, 5f)),
        };

        var info = await _query.QueryAsync(game.Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal("Pulsar !dlrow[31m", info.Name); // control characters and direction overrides removed
        Assert.Equal("de_dust2", info.Map);
        Assert.Equal((2, 16, 1), (info.Players, info.MaxPlayers, info.Bots));
        Assert.Equal("1.40.2.1", info.Version);
        Assert.Equal([new QueryPlayer("Ana", 12, TimeSpan.FromSeconds(61)), new QueryPlayer("Bobby", -3, TimeSpan.FromSeconds(5))],
            info.PlayerList);
        Assert.Equal(2, game.Challenged); // both requests were sent again with the challenge
    }

    [Fact]
    public async Task AGameOnEveryInterfaceIsQueriedWhereItAnswers()
    {
        await using var game = new FakeA2sServer { Info = Info("Anywhere", "de_dust2", players: 0, max: 10, bots: 0, "1.0") };

        var info = await _query.QueryAsync(game.Endpoint with { Address = PortAssignment.AnyAddress }, TestContext.Current.CancellationToken);

        Assert.Equal("Anywhere", info.Name);
    }

    [Fact]
    public async Task MinecraftOnEveryInterfaceIsAskedOnLoopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = ServeMinecraftAsync(listener, """{"version":{"name":"26.3"},"players":{"max":5,"online":0},"description":"hi"}""");

        var info = await _query.QueryAsync(new QueryEndpoint(QueryProtocol.Minecraft, PortAssignment.AnyAddress, port), TestContext.Current.CancellationToken);

        Assert.Equal("hi", info.Name);
        await served;
    }

    [Fact]
    public async Task ASplitAnswerIsPutBackTogether()
    {
        var names = Enumerable.Range(1, 120).Select(i => ($"player-with-a-long-name-{i:D3}", i, 10f)).ToArray();
        await using var game = new FakeA2sServer
        {
            Info = Info("Big", "Procedural Map", players: 120, max: 200, bots: 0, "2593"),
            Players = PlayersPayload(names),
            SplitPlayers = true,
        };

        var info = await _query.QueryAsync(game.Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal(120, info.PlayerList.Count);
        Assert.Equal("player-with-a-long-name-120", info.PlayerList[^1].Name);
    }

    [Fact]
    public async Task RustCountsPastWhatAByteHolds()
    {
        await using var game = new FakeA2sServer
        {
            Info = Info("Rust", "Procedural Map", players: 255, max: 255, bots: 0, "2593", keywords: "biweekly,cp312,mp500,v2593"),
        };

        var info = await _query.QueryAsync(game.Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal((312, 500), (info.Players, info.MaxPlayers));
        Assert.Empty(info.PlayerList); // the players request went unanswered: the counts are still there
    }

    [Fact]
    public async Task CountsInOtherDigitsAreNotTaken()
    {
        // "cp٣١٢" (Arabic-Indic digits): the counts the info gave stay.
        await using var game = new FakeA2sServer
        {
            Info = Info("Rust", "Procedural Map", players: 7, max: 50, bots: 0, "2593", keywords: "biweekly,cp٣١٢,mp٥٠٠,v2593"),
        };

        var info = await _query.QueryAsync(game.Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal((7, 50), (info.Players, info.MaxPlayers));
    }

    [Fact]
    public async Task AGameThatDoesNotAnswerIsAQueryFailure()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        var query = new ServerQuery(TimeProvider.System) { Timeout = TimeSpan.FromMilliseconds(300) };

        await Assert.ThrowsAsync<QueryException>(() =>
            query.QueryAsync(new QueryEndpoint(QueryProtocol.A2s, "127.0.0.1", port), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MinecraftGivesItsMessageCountsAndSample()
    {
        const string Status = """
            {"version":{"name":"26.3","protocol":775},
             "players":{"max":20,"online":2,"sample":[{"name":"Steve","id":"4566e69f-c907-48ee-8d71-d7ba5aa00d20"},{"name":"§cAlex","id":"x"}]},
             "description":{"text":"A ","extra":[{"text":"§aPulsar","bold":true},"\nserver"]},
             "favicon":"data:image/png;base64,AAAA"}
            """;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = ServeMinecraftAsync(listener, Status);

        var info = await _query.QueryAsync(new QueryEndpoint(QueryProtocol.Minecraft, "127.0.0.1", port), TestContext.Current.CancellationToken);

        Assert.Equal("A Pulsar server", info.Name);
        Assert.Null(info.Map);
        Assert.Equal((2, 20), (info.Players, info.MaxPlayers));
        Assert.Equal("26.3", info.Version);
        Assert.Equal(["Steve", "Alex"], info.PlayerList.Select(p => p.Name));
        Assert.Equal(port, await served); // the handshake named the port it was sent to
    }

    [Fact]
    public async Task SomethingElseOnAMinecraftPortIsAQueryFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await client.GetStream().WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<QueryException>(() =>
            _query.QueryAsync(new QueryEndpoint(QueryProtocol.Minecraft, "127.0.0.1", port), TestContext.Current.CancellationToken));
        await served;
    }

    [Theory]
    [InlineData("  plain  ", "plain")]
    [InlineData("§aGreen§r name", "Green name")]
    [InlineData("‮evil⁦", "evil")]
    [InlineData("\u0007\u0000", null)]
    [InlineData("", null)]
    public void QueryTextIsCleaned(string text, string? expected)
    {
        Assert.Equal(expected, QueryText.Clean(text));
    }

    [Fact]
    public void QueryTextIsCutWithoutSplittingACharacter()
    {
        var text = new string('a', QueryText.MaxLength - 1) + "\U0001F600";

        Assert.Equal(new string('a', QueryText.MaxLength - 1), QueryText.Clean(text));
        Assert.Equal(QueryText.MaxLength, QueryText.Clean(new string('b', 500))!.Length);
    }

    private static async Task<int> ServeMinecraftAsync(TcpListener listener, string status)
    {
        using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        var handshake = new byte[await ReadVarIntAsync(stream)];
        await stream.ReadExactlyAsync(handshake, TestContext.Current.CancellationToken);
        var request = new byte[await ReadVarIntAsync(stream)];
        await stream.ReadExactlyAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal([0x00], request);
        Assert.Equal(1, handshake[^1]); // next state: status
        var port = (handshake[^3] << 8) | handshake[^2];

        var json = Encoding.UTF8.GetBytes(status);
        var packet = new List<byte> { 0x00 };
        packet.AddRange(VarInt(json.Length));
        packet.AddRange(json);
        await stream.WriteAsync(VarInt(packet.Count).Concat(packet).ToArray(), TestContext.Current.CancellationToken);
        return port;
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream)
    {
        var value = 0;
        var buffer = new byte[1];
        for (var shift = 0; ; shift += 7)
        {
            await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
            value |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
        }
    }

    private static byte[] VarInt(int value)
    {
        var bytes = new List<byte>();
        var remaining = (uint)value;
        do
        {
            var current = (byte)(remaining & 0x7F);
            remaining >>= 7;
            bytes.Add(remaining == 0 ? current : (byte)(current | 0x80));
        }
        while (remaining != 0);
        return [.. bytes];
    }

    private static byte[] Info(string name, string map, byte players, byte max, byte bots, string version, string? keywords = null)
    {
        var data = new List<byte> { 0x49, 17 };
        data.AddRange(Text(name));
        data.AddRange(Text(map));
        data.AddRange(Text("cstrike"));
        data.AddRange(Text("Counter-Strike: Source"));
        data.AddRange([0xF0, 0x00]); // app ID 240
        data.AddRange([players, max, bots, (byte)'d', (byte)'w', 0, 1]);
        data.AddRange(Text(version));
        if (keywords is not null)
        {
            data.Add(0x80 | 0x20); // game port, keywords
            data.AddRange([0x87, 0x69]);
            data.AddRange(Text(keywords));
        }

        return [.. data];
    }

    private static byte[] PlayersPayload(params (string Name, int Score, float Seconds)[] players)
    {
        var data = new List<byte> { 0x44, (byte)players.Length };
        for (var i = 0; i < players.Length; i++)
        {
            data.Add((byte)i);
            data.AddRange(Text(players[i].Name));
            var numbers = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(numbers, players[i].Score);
            BinaryPrimitives.WriteSingleLittleEndian(numbers.AsSpan(4), players[i].Seconds);
            data.AddRange(numbers);
        }

        return [.. data];
    }

    private static byte[] Text(string text) => [.. Encoding.UTF8.GetBytes(text), 0];

    /// <summary>A game answering A2S as Source does: a challenge first, then the answer, the players split if asked.</summary>
    private sealed class FakeA2sServer : IAsyncDisposable
    {
        private static readonly byte[] s_challenge = [0x12, 0x34, 0x56, 0x78];

        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public FakeA2sServer() => _serving = ServeAsync();

        public required byte[] Info { get; init; }

        /// <summary>Null: the players request is never answered.</summary>
        public byte[]? Players { get; init; }

        public bool SplitPlayers { get; init; }

        public int Challenged { get; private set; }

        public QueryEndpoint Endpoint => new(QueryProtocol.A2s, "127.0.0.1", ((IPEndPoint)_socket.Client.LocalEndPoint!).Port);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _socket.Dispose();
            try
            {
                await _serving;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(_stop.Token);
                var request = received.Buffer.AsSpan(4).ToArray();
                byte[]? answer = null;
                if (request[0] == 0x54)
                {
                    answer = request.AsSpan(request.Length - 4).SequenceEqual(s_challenge) ? Info : Challenge();
                }
                else if (request[0] == 0x55 && Players is { } players)
                {
                    answer = request.AsSpan(1).SequenceEqual(s_challenge) ? players : Challenge();
                }

                if (answer is null)
                {
                    continue;
                }

                if (answer == Info || answer == Players)
                {
                    Challenged++;
                }

                var packet = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.Concat(answer).ToArray();
                if (SplitPlayers && answer == Players)
                {
                    // Out of order, as UDP may deliver them.
                    var chunks = packet.Chunk(1200).ToArray();
                    foreach (var index in Enumerable.Range(0, chunks.Length).Reverse())
                    {
                        byte[] header = [0xFE, 0xFF, 0xFF, 0xFF, 0x2A, 0x00, 0x00, 0x00, (byte)chunks.Length, (byte)index, 0xE0, 0x04];
                        await _socket.SendAsync(header.Concat(chunks[index]).ToArray().AsMemory(), received.RemoteEndPoint, _stop.Token);
                    }

                    continue;
                }

                await _socket.SendAsync(packet, received.RemoteEndPoint, _stop.Token);
            }
        }

        private static byte[] Challenge() => [0x41, .. s_challenge];
    }
}
