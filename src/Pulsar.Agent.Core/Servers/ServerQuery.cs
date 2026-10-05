using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>Asks a running game for its name, map and players (the manifest's <c>query</c>).</summary>
public interface IServerQuery
{
    /// <exception cref="QueryException">The game did not answer in time, or answered something unreadable.</exception>
    Task<ServerQueryInfo> QueryAsync(QueryEndpoint endpoint, CancellationToken cancellationToken);
}

public sealed class QueryException : Exception
{
    public QueryException()
    {
    }

    public QueryException(string message)
        : base(message)
    {
    }

    public QueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The query protocols of docs/architecture/04 ("Query"): A2S over UDP and Minecraft's Server List Ping over TCP.
/// Answers are read with fixed limits (packet count, sizes, players) and every text is cleaned
/// (<see cref="QueryText"/>): the game, and its players' names, are not trusted.
/// </summary>
public sealed class ServerQuery(TimeProvider clock) : IServerQuery
{
    /// <summary>For the whole query (A2S: the info, then the players).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<ServerQueryInfo> QueryAsync(QueryEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return endpoint.Protocol switch
            {
                QueryProtocol.Minecraft => await MinecraftPing.QueryAsync(endpoint, clock, timeout.Token),
                _ => await A2sQuery.QueryAsync(endpoint, clock, timeout.Token),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QueryException($"No answer from {endpoint.Address}:{endpoint.Port} within {Timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new QueryException($"Cannot query {endpoint.Address}:{endpoint.Port}: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Valve's server queries (developer.valvesoftware.com/wiki/Server_queries): A2S_INFO, then A2S_PLAYER, each answered
/// with a challenge first when the game asks for one; answers split over several packets are put back together.
/// </summary>
internal static partial class A2sQuery
{
    private const int SinglePacket = -1;
    private const int SplitPacket = -2;
    private const byte Challenge = 0x41;
    private const byte Info = 0x49;
    private const byte Players = 0x44;
    private const int MaxParts = 32;

    private static readonly byte[] s_noChallenge = [0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>The address each game port on every interface answered on last (<see cref="AnsweringAddressAsync"/>).</summary>
    private static readonly ConcurrentDictionary<int, IPAddress> s_answering = new();

    /// <summary>The players have this long once the info came: a game that does not list them still gets its counts shown.</summary>
    private static readonly TimeSpan s_playersTimeout = TimeSpan.FromSeconds(1);

    public static async Task<ServerQueryInfo> QueryAsync(QueryEndpoint endpoint, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(endpoint.Address == PortAssignment.AnyAddress ? await AnsweringAddressAsync(endpoint.Port, cancellationToken) : IPAddress.Parse(endpoint.Address),
            endpoint.Port);
        // A2S_INFO takes the challenge after its payload, A2S_PLAYER in place of its -1.
        var info = await RequestAsync(udp, challenge => [0x54, .. "Source Engine Query\0"u8, .. challenge], Info, cancellationToken);
        if (endpoint.Address == PortAssignment.AnyAddress)
        {
            s_answering[endpoint.Port] = ((IPEndPoint)udp.Client.RemoteEndPoint!).Address;
        }

        var at = clock.GetUtcNow();
        var (name, map, players, maxPlayers, bots, version) = ReadInfo(info);
        IReadOnlyList<QueryPlayer> list = [];
        using var playersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        playersTimeout.CancelAfter(s_playersTimeout);
        try
        {
            var answer = await RequestAsync(udp, challenge => [0x55, .. challenge.Length == 0 ? s_noChallenge : challenge], Players, playersTimeout.Token);
            list = ReadPlayers(answer);
        }
        catch (Exception ex) when (ex is QueryException or SocketException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Some games answer the info only; the counts are still worth showing.
        }

        return new ServerQueryInfo(at, name, map, players, maxPlayers, bots, version, list);
    }

    /// <summary>
    /// The local address a game listening on every interface answers on: loopback for most, but srcds ignores queries
    /// from loopback and answers on the machine's other addresses. The first query goes to all of them at once; the
    /// address that answers is kept for the next ones, and asked again when it stops answering.
    /// </summary>
    private static async Task<IPAddress> AnsweringAddressAsync(int port, CancellationToken cancellationToken)
    {
        if (s_answering.TryGetValue(port, out var known))
        {
            s_answering.TryRemove(port, out _); // put back below once it answers again
            return known;
        }

        var candidates = new List<IPAddress> { IPAddress.Loopback };
        candidates.AddRange(NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct());
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        byte[] request = [0xFF, 0xFF, 0xFF, 0xFF, 0x54, .. "Source Engine Query\0"u8];
        foreach (var candidate in candidates)
        {
            try
            {
                await probe.SendAsync(request, new IPEndPoint(candidate, port), cancellationToken);
            }
            catch (SocketException)
            {
                // An address that cannot be reached right now.
            }
        }

        while (true)
        {
            UdpReceiveResult answer;
            try
            {
                answer = await probe.ReceiveAsync(cancellationToken);
            }
            catch (SocketException)
            {
                continue; // ICMP "port unreachable" from one of the addresses
            }

            if (answer.RemoteEndPoint.Port == port && candidates.Contains(answer.RemoteEndPoint.Address))
            {
                return answer.RemoteEndPoint.Address;
            }
        }
    }

    /// <summary>
    /// Sends a request and returns the answer's payload (after its type byte); a challenge answer is sent back in a
    /// new request (<paramref name="request"/> builds it, first without one).
    /// </summary>
    private static async Task<byte[]> RequestAsync(UdpClient udp, Func<byte[], byte[]> request, byte expected, CancellationToken cancellationToken)
    {
        byte[] challenge = [];
        for (var round = 0; round < 3; round++)
        {
            await udp.SendAsync(Packet(request(challenge)), cancellationToken);
            var payload = await ReceiveAsync(udp, cancellationToken);
            if (payload.Length == 0)
            {
                throw new QueryException("An empty answer.");
            }

            if (payload[0] == expected)
            {
                return payload[1..];
            }

            if (payload[0] != Challenge || payload.Length < 5)
            {
                throw new QueryException($"An unexpected answer (0x{payload[0]:X2}).");
            }

            challenge = payload[1..5];
        }

        throw new QueryException("The game kept asking for a challenge.");
    }

    private static byte[] Packet(byte[] request) => [0xFF, 0xFF, 0xFF, 0xFF, .. request];

    /// <summary>One answer: a single packet, or every part of a split one (Source format).</summary>
    private static async Task<byte[]> ReceiveAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        var first = (await udp.ReceiveAsync(cancellationToken)).Buffer;
        if (first.Length < 5)
        {
            throw new QueryException("A truncated answer.");
        }

        var header = BinaryPrimitives.ReadInt32LittleEndian(first);
        if (header == SinglePacket)
        {
            return first[4..];
        }

        if (header != SplitPacket)
        {
            throw new QueryException("An answer that is not A2S.");
        }

        byte[]?[] parts = [];
        int? id = null;
        var datagram = first;
        while (true)
        {
            // Header (4), ID (4), total (1), number (1), maximum size (2), then the part.
            if (datagram.Length < 12 || BinaryPrimitives.ReadInt32LittleEndian(datagram) != SplitPacket)
            {
                throw new QueryException("A truncated split answer.");
            }

            var partId = BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(4));
            if (partId < 0)
            {
                throw new QueryException("A compressed answer, which only very old games send.");
            }

            var total = datagram[8];
            var number = datagram[9];
            if (id is null)
            {
                if (total is 0 or > MaxParts)
                {
                    throw new QueryException("An answer split in too many parts.");
                }

                id = partId;
                parts = new byte[total][];
            }

            if (partId == id && total == parts.Length && number < total)
            {
                parts[number] = datagram[12..];
            }

            if (parts.All(p => p is not null))
            {
                var whole = parts.SelectMany(p => p!).ToArray();
                if (whole.Length < 5 || BinaryPrimitives.ReadInt32LittleEndian(whole) != SinglePacket)
                {
                    throw new QueryException("A split answer that does not add up.");
                }

                return whole[4..];
            }

            datagram = (await udp.ReceiveAsync(cancellationToken)).Buffer;
        }
    }

    private static (string? Name, string? Map, int Players, int MaxPlayers, int Bots, string? Version) ReadInfo(byte[] payload)
    {
        var reader = new Reader(payload);
        reader.Byte(); // protocol
        var name = reader.String();
        var map = reader.String();
        reader.String(); // folder
        reader.String(); // game
        var appId = reader.Short();
        int players = reader.Byte();
        int maxPlayers = reader.Byte();
        int bots = reader.Byte();
        reader.Byte(); // server type
        reader.Byte(); // environment
        reader.Byte(); // visibility
        reader.Byte(); // VAC
        if (appId == 2400)
        {
            reader.Skip(3); // The Ship
        }

        var version = reader.String();
        if (reader.Remaining > 0)
        {
            var extra = reader.Byte();
            if ((extra & 0x80) != 0)
            {
                reader.Skip(2); // game port
            }

            if ((extra & 0x10) != 0)
            {
                reader.Skip(8); // Steam ID
            }

            if ((extra & 0x40) != 0)
            {
                reader.Skip(2);
                reader.String(); // SourceTV
            }

            if ((extra & 0x20) != 0 && reader.String() is { } keywords)
            {
                // Rust counts past 255 in its keywords: "cp312,mp500".
                if (CurrentPlayers().Match(keywords) is { Success: true } current)
                {
                    players = int.Parse(current.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                }

                if (MaximumPlayers().Match(keywords) is { Success: true } maximum)
                {
                    maxPlayers = int.Parse(maximum.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                }
            }
        }

        return (QueryText.Clean(name), QueryText.Clean(map), players, maxPlayers, Math.Min(bots, players), QueryText.Clean(version));
    }

    /// <summary>Players read to the end of the answer: the count byte wraps past 255.</summary>
    private static List<QueryPlayer> ReadPlayers(byte[] payload)
    {
        var reader = new Reader(payload);
        reader.Byte(); // count
        var players = new List<QueryPlayer>();
        while (reader.Remaining > 0 && players.Count < QueryText.MaxPlayers)
        {
            reader.Byte(); // index
            var name = QueryText.Clean(reader.String());
            var score = reader.Int();
            var seconds = reader.Float();
            if (name is null)
            {
                continue; // still connecting
            }

            players.Add(new QueryPlayer(name, score,
                float.IsFinite(seconds) && seconds is >= 0 and < 100 * 24 * 3600 ? TimeSpan.FromSeconds(Math.Floor(seconds)) : null));
        }

        return players;
    }

    [GeneratedRegex(@"(?:^|,)cp(\d{1,6})(?:,|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CurrentPlayers();

    [GeneratedRegex(@"(?:^|,)mp(\d{1,6})(?:,|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MaximumPlayers();

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public readonly int Remaining => _data.Length - _offset;

        public byte Byte() => Take(1)[0];

        public short Short() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public void Skip(int count) => Take(count);

        public string String()
        {
            var end = _data[_offset..].IndexOf((byte)0);
            if (end < 0)
            {
                throw new QueryException("A truncated answer.");
            }

            var text = Encoding.UTF8.GetString(_data.Slice(_offset, end));
            _offset += end + 1;
            return text;
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (Remaining < count)
            {
                throw new QueryException("A truncated answer.");
            }

            var span = _data.Slice(_offset, count);
            _offset += count;
            return span;
        }
    }
}

/// <summary>
/// Minecraft's Server List Ping (minecraft.wiki/w/Java_Edition_protocol/Server_List_Ping): a handshake for the status
/// state, a status request, and a JSON answer with the version, the player counts, a sample of players and the message
/// of the day.
/// </summary>
internal static class MinecraftPing
{
    /// <summary>Bigger than any status answer, server icon included (a 64×64 PNG).</summary>
    private const int MaxAnswer = 256 * 1024;

    public static async Task<ServerQueryInfo> QueryAsync(QueryEndpoint endpoint, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        var address = endpoint.Address == PortAssignment.AnyAddress ? IPAddress.Loopback : IPAddress.Parse(endpoint.Address);
        await client.ConnectAsync(address, endpoint.Port, cancellationToken);
        var stream = client.GetStream();

        var handshake = new List<byte>();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, -1); // any version: a status request
        var host = Encoding.UTF8.GetBytes(endpoint.Address);
        WriteVarInt(handshake, host.Length);
        handshake.AddRange(host);
        handshake.Add((byte)(endpoint.Port >> 8));
        handshake.Add((byte)endpoint.Port);
        WriteVarInt(handshake, 1); // next state: status
        var request = new List<byte>();
        WriteVarInt(request, handshake.Count);
        request.AddRange(handshake);
        request.AddRange([0x01, 0x00]); // status request: length 1, packet 0
        await stream.WriteAsync(request.ToArray(), cancellationToken);

        var length = await ReadVarIntAsync(stream, cancellationToken);
        if (length is <= 0 or > MaxAnswer)
        {
            throw new QueryException("An answer of an unexpected size.");
        }

        var packet = new byte[length];
        await stream.ReadExactlyAsync(packet, cancellationToken);
        var offset = 0;
        if (ReadVarInt(packet, ref offset) != 0x00)
        {
            throw new QueryException("An answer that is not a status.");
        }

        var textLength = ReadVarInt(packet, ref offset);
        if (textLength < 0 || textLength > packet.Length - offset)
        {
            throw new QueryException("A truncated answer.");
        }

        var at = clock.GetUtcNow();
        try
        {
            return Read(JsonDocument.Parse(packet.AsMemory(offset, textLength), new JsonDocumentOptions { MaxDepth = 16 }).RootElement, at);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new QueryException("An answer that is not a Minecraft status.", ex);
        }
    }

    private static ServerQueryInfo Read(JsonElement status, DateTimeOffset at)
    {
        var players = status.TryGetProperty("players", out var p) ? p : default;
        var sample = new List<QueryPlayer>();
        if (players.ValueKind == JsonValueKind.Object && players.TryGetProperty("sample", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var player in list.EnumerateArray().Take(QueryText.MaxPlayers))
            {
                if (player.ValueKind == JsonValueKind.Object && player.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String && QueryText.Clean(name.GetString()) is { } clean)
                {
                    sample.Add(new QueryPlayer(clean, null, null));
                }
            }
        }

        var version = status.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("name", out var versionName)
            && versionName.ValueKind == JsonValueKind.String ? versionName.GetString() : null;
        var motd = new StringBuilder();
        if (status.TryGetProperty("description", out var description))
        {
            AppendText(description, motd, depth: 0);
        }

        return new ServerQueryInfo(at, QueryText.Clean(motd.Replace('\n', ' ').ToString()), Map: null, Count(players, "online"), Count(players, "max"), Bots: 0,
            QueryText.Clean(version), sample);
    }

    private static int Count(JsonElement players, string name) =>
        players.ValueKind == JsonValueKind.Object && players.TryGetProperty(name, out var count) && count.TryGetInt32(out var value) && value >= 0 ? value : 0;

    /// <summary>A chat component's text: a string, or <c>text</c> then each of <c>extra</c>.</summary>
    private static void AppendText(JsonElement component, StringBuilder text, int depth)
    {
        if (depth > 8 || text.Length > QueryText.MaxLength * 4)
        {
            return;
        }

        switch (component.ValueKind)
        {
            case JsonValueKind.String:
                text.Append(component.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var part in component.EnumerateArray())
                {
                    AppendText(part, text, depth + 1);
                }

                break;
            case JsonValueKind.Object:
                if (component.TryGetProperty("text", out var own))
                {
                    AppendText(own, text, depth + 1);
                }

                if (component.TryGetProperty("extra", out var extra))
                {
                    AppendText(extra, text, depth + 1);
                }

                break;
        }
    }

    private static void WriteVarInt(List<byte> buffer, int value)
    {
        var remaining = (uint)value;
        do
        {
            var current = (byte)(remaining & 0x7F);
            remaining >>= 7;
            buffer.Add(remaining == 0 ? current : (byte)(current | 0x80));
        }
        while (remaining != 0);
    }

    private static int ReadVarInt(byte[] data, ref int offset)
    {
        var value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= data.Length)
            {
                throw new QueryException("A truncated answer.");
            }

            var current = data[offset++];
            value |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new QueryException("A malformed answer.");
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = 0;
        var buffer = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            value |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new QueryException("A malformed answer.");
    }
}
