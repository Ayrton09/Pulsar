using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// GoldSrc's RCON over UDP, as hlds speaks it (checked against hlds 1.1.2.7 build 10210): <c>challenge rcon</c> is answered
/// with <c>challenge rcon &lt;number&gt;</c>, then each command goes as <c>rcon &lt;number&gt; "&lt;password&gt;" &lt;command&gt;</c>. An
/// answer is one or more packets <c>\xFF\xFF\xFF\xFF l &lt;text&gt;</c>, some split in parts
/// (<c>\xFE\xFF\xFF\xFF &lt;id&gt; &lt;part number &lt;&lt; 4 | parts&gt;</c>), read until none came for a moment; a wrong password is
/// answered <c>Bad rcon_password.</c>. The game shares this port with its players, so anything may write to it: the
/// connected socket takes only the game's address, and the port must be held by the server's own game before the
/// password is sent (docs/architecture/08, T18).
/// </summary>
public sealed class GoldSrcRconConnector : IRconConnector
{
    /// <summary>How long the game may take to answer the challenge, and to start answering a command.</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long after the last packet an answer is considered complete.</summary>
    public TimeSpan AnswerIdle { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Who holds which UDP port (Windows' UDP table); null checks nothing (tests).</summary>
    public ILocalPeers? Peers { get; init; }

    public async Task<IRconSession> ConnectAsync(RconEndpoint endpoint, ProcessIdentity owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(owner);
        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Connect(IPAddress.Parse(endpoint.Address), endpoint.Port);
            // Checked again before every command: UDP has no connection whose other end stays the same.
            var peers = Peers;
            void Verify() => RconPeers.VerifyUdp(peers, endpoint, owner);
            Verify();
            var session = new GoldSrcRconSession(udp, endpoint.Password, AnswerTimeout, AnswerIdle, Verify);
            await session.LogOnAsync(cancellationToken);
            return session;
        }
        catch (SocketException ex)
        {
            udp.Dispose();
            throw new RconException($"Cannot reach RCON at {endpoint.Address}:{endpoint.Port}.", ex);
        }
        catch
        {
            udp.Dispose();
            throw;
        }
    }
}

/// <param name="verify">Throws unless the game's port is still held by the server's game alone; run before the password is sent.</param>
internal sealed class GoldSrcRconSession(UdpClient udp, string password, TimeSpan answerTimeout, TimeSpan answerIdle, Action verify) : IRconSession
{
    private const int SinglePacket = -1;
    private const int SplitPacket = -2;
    private const byte Print = (byte)'l';
    private const int MaxAnswerLength = 1024 * 1024;
    private const string BadPassword = "Bad rcon_password.";

    private const string BadChallenge = "Bad challenge";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<int, byte[]?[]> _pending = []; // split packets by their id, until every part came
    private string? _challenge;
    private bool _closed;

    /// <summary>Gets the game's challenge, then checks the password with a command that prints nothing of note.</summary>
    public async Task LogOnAsync(CancellationToken cancellationToken)
    {
        await SendAsync("challenge rcon", cancellationToken);
        using (var timeout = Timeout(answerTimeout, cancellationToken))
        {
            while (_challenge is null)
            {
                var payload = await ReceiveAsync(timeout.Token, cancellationToken);
                var text = Encoding.ASCII.GetString(payload).TrimEnd('\0', '\n');
                if (text.StartsWith("challenge rcon ", StringComparison.Ordinal) && uint.TryParse(text.AsSpan(15), out var number))
                {
                    _challenge = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }

        // The password is checked by the game only with a command: one whose answer is empty.
        await ExecuteAsync("echo", cancellationToken);
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length is 0 or > 1024 || command.Any(char.IsControl))
        {
            throw new ArgumentException("A command is a single line of up to 1024 characters.", nameof(command));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_closed)
            {
                throw new RconException("The RCON session was closed.") { ConnectionLost = true, NotSent = true };
            }

            Drain(); // the late end of an earlier answer is not this one's
            try
            {
                verify();
            }
            catch (RconException)
            {
                // Something else took the port since the last command: nothing more goes there.
                _closed = true;
                throw;
            }

            await SendAsync($"rcon {_challenge} \"{password}\" {command}", cancellationToken);
            using var deadline = Timeout(answerTimeout * 2, cancellationToken);
            var answer = new StringBuilder();
            using (var first = Timeout(answerTimeout, deadline.Token))
            {
                try
                {
                    answer.Append(await ReceiveAnswerAsync(first.Token, cancellationToken));
                }
                catch (RconException) when (EndsTheGame(command))
                {
                    return ""; // hlds answers quit with nothing: it is closing
                }
            }

            if (answer.ToString().StartsWith(BadPassword, StringComparison.Ordinal))
            {
                throw new RconException("The RCON password was refused.") { AuthenticationFailed = true };
            }

            if (answer.ToString().StartsWith(BadChallenge, StringComparison.Ordinal))
            {
                // The game restarted, or forgot the challenge: a new session asks for a new one. The command did not run.
                _closed = true;
                throw new RconException("The game no longer takes this RCON challenge.") { ConnectionLost = true, NotSent = true };
            }

            while (answer.Length < MaxAnswerLength)
            {
                using var idle = Timeout(answerIdle, deadline.Token);
                try
                {
                    answer.Append(await ReceiveAnswerAsync(idle.Token, cancellationToken, idleEndsAnswer: true));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!deadline.IsCancellationRequested)
                    {
                        return answer.ToString(); // idle: the answer is complete
                    }

                    break;
                }
            }

            // A game that keeps answering: what came so far, and a session no one reads from.
            _closed = true;
            udp.Dispose();
            return answer.Length > MaxAnswerLength ? answer.ToString(0, MaxAnswerLength) : answer.ToString();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Whether <paramref name="command"/> shuts the game down, which then answers nothing.</summary>
    internal static bool EndsTheGame(string command)
    {
        var name = command.Split(' ', 2)[0];
        return name.Equals("quit", StringComparison.OrdinalIgnoreCase) || name.Equals("exit", StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        udp.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The text of one printed packet, or of every part of a split one. Parts of one split packet arrive together; a part
    /// of another one, or an answer that is not printed text, ends nothing and is skipped.
    /// </summary>
    internal static string? Text(byte[] packet, Dictionary<int, byte[]?[]> pending)
    {
        if (packet.Length < 5)
        {
            return null;
        }

        var header = BinaryPrimitives.ReadInt32LittleEndian(packet);
        byte[] payload;
        if (header == SinglePacket)
        {
            payload = packet[4..];
        }
        else if (header == SplitPacket && packet.Length >= 9)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));
            var total = packet[8] & 0x0F;
            var number = packet[8] >> 4;
            if (total == 0 || number >= total)
            {
                return null;
            }

            if (!pending.TryGetValue(id, out var parts) || parts.Length != total)
            {
                if (pending.Count >= 8)
                {
                    pending.Clear(); // parts that never completed
                }

                pending[id] = parts = new byte[total][];
            }

            parts[number] = packet[9..];
            if (parts.Any(p => p is null))
            {
                return null;
            }

            pending.Remove(id);
            var whole = parts.SelectMany(p => p!).ToArray();
            if (whole.Length < 5 || BinaryPrimitives.ReadInt32LittleEndian(whole) != SinglePacket)
            {
                return null;
            }

            payload = whole[4..];
        }
        else
        {
            return null;
        }

        if (payload[0] != Print)
        {
            return null;
        }

        var text = payload.AsSpan(1);
        var end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }

    private async Task<string> ReceiveAnswerAsync(CancellationToken wait, CancellationToken cancellationToken, bool idleEndsAnswer = false)
    {
        while (true)
        {
            var packet = await ReceiveAsync(wait, cancellationToken, raw: true, idleEndsAnswer);
            if (Text(packet, _pending) is { } text)
            {
                return text;
            }
        }
    }

    /// <summary>One datagram from the game: its payload after the -1 header, or (<paramref name="raw"/>) the whole of it.</summary>
    private async Task<byte[]> ReceiveAsync(CancellationToken wait, CancellationToken cancellationToken, bool raw = false, bool idleEndsAnswer = false)
    {
        while (true)
        {
            try
            {
                var datagram = (await udp.ReceiveAsync(wait)).Buffer;
                if (raw)
                {
                    return datagram;
                }

                if (datagram.Length > 4 && BinaryPrimitives.ReadInt32LittleEndian(datagram) == SinglePacket)
                {
                    return datagram[4..];
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (idleEndsAnswer)
                {
                    throw;
                }

                throw new RconException("The game did not answer over RCON in time.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP "port unreachable": nothing listens there (any more). Between the packets of an answer, it ends.
                if (idleEndsAnswer)
                {
                    throw new OperationCanceledException(ex.Message, ex, wait);
                }

                throw new RconException("Nothing listens for RCON there.", ex) { ConnectionLost = _challenge is not null };
            }
            catch (ObjectDisposedException ex)
            {
                throw new RconException("The RCON session was closed.", ex) { ConnectionLost = true };
            }
        }
    }

    private async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        byte[] packet = [0xFF, 0xFF, 0xFF, 0xFF, .. Encoding.UTF8.GetBytes(text + "\n")];
        try
        {
            await udp.SendAsync(packet, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new RconException("RCON could not be sent.", ex) { ConnectionLost = true };
        }
    }

    /// <summary>Drops what arrived since the last answer ended.</summary>
    private void Drain()
    {
        try
        {
            while (udp.Available > 0)
            {
                IPEndPoint? from = null;
                udp.Receive(ref from);
            }
        }
        catch (SocketException)
        {
            // An ICMP error waiting in the socket: nothing to keep.
        }

        _pending.Clear();
    }

    private static CancellationTokenSource Timeout(TimeSpan after, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(after);
        return source;
    }
}
