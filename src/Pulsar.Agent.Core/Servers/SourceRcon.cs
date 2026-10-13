using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>Opens remote console sessions to the games the agent runs (<c>logfile+rcon</c> consoles).</summary>
public interface IRconConnector
{
    /// <param name="owner">
    /// The account the server's game runs as: what listens at <paramref name="endpoint"/> must run as it before the
    /// password is sent (docs/architecture/08, T18).
    /// </param>
    /// <exception cref="RconException">
    /// The game refused the connection or the password, or did not answer; or what listens there is not the game.
    /// </exception>
    Task<IRconSession> ConnectAsync(RconEndpoint endpoint, ProcessIdentity owner, CancellationToken cancellationToken);
}

public interface IRconSession : IAsyncDisposable
{
    /// <summary>Runs one command and returns what the game answered (possibly nothing).</summary>
    /// <exception cref="RconException">The connection broke or the game did not answer in time.</exception>
    Task<string> ExecuteAsync(string command, CancellationToken cancellationToken);
}

public sealed class RconException : Exception
{
    public RconException()
    {
    }

    public RconException(string message)
        : base(message)
    {
    }

    public RconException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The password was refused (as opposed to a game not listening yet).</summary>
    public bool AuthenticationFailed { get; init; }

    /// <summary>The connection was closed (as opposed to an answer that did not come in time).</summary>
    public bool ConnectionLost { get; init; }

    /// <summary>What listens at the endpoint is not the server's game: nothing was sent to it.</summary>
    public bool NotTheGame { get; init; }

    /// <summary>The command never left the agent (the connection was closed already): sending it again cannot run it twice.</summary>
    public bool NotSent { get; init; }
}

/// <summary>
/// The Source RCON protocol (developer.valvesoftware.com/wiki/Source_RCON_Protocol) as srcds and CS2 speak it (spike
/// 0001): little-endian packets <c>size, id, type, body\0, \0</c>. Authentication is answered with one or two packets
/// (srcds sends an empty response first, CS2 does not); a command's answer is one or more response packets, read until
/// the connection has been idle for a moment (CS2 garbles the usual empty "end marker" request). An answer that never
/// pauses is cut at twice <see cref="AnswerTimeout"/> or 1 MiB, and its session closed (what is still coming belongs to it).
/// </summary>
public sealed class SourceRconConnector : IRconConnector
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the game may take to start answering (and to accept the password). ARK: Survival Ascended takes 2 to
    /// 4 s, and more while it hitches, even when nobody plays (Phase 12).
    /// </summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long after the last packet an answer is considered complete.</summary>
    public TimeSpan AnswerIdle { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Who listens where (Windows' TCP table); null checks nothing (tests).</summary>
    public ILocalPeers? Peers { get; init; }

    public async Task<IRconSession> ConnectAsync(RconEndpoint endpoint, ProcessIdentity owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(owner);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await client.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token);
                }
                catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    throw new RconException($"Cannot connect to RCON at {endpoint.Address}:{endpoint.Port}.", ex);
                }
            }

            RconPeers.Verify(Peers, client.Client, endpoint, owner);
            var session = new SourceRconSession(client, AnswerTimeout, AnswerIdle);
            await session.AuthenticateAsync(endpoint.Password, cancellationToken);
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed class SourceRconSession(TcpClient client, TimeSpan answerTimeout, TimeSpan answerIdle) : IRconSession
{
    private const int Auth = 3;
    private const int AuthResponse = 2;
    private const int ExecCommand = 2;
    private const int MaxPacketSize = 64 * 1024;
    private const int MaxAnswerLength = 1024 * 1024;

    private readonly NetworkStream _stream = client.GetStream();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId = 1;
    private bool _closed; // an answer was cut: the rest of it would be read as the next one's

    public async Task AuthenticateAsync(string password, CancellationToken cancellationToken)
    {
        var id = _nextId++;
        await SendAsync(id, Auth, password, cancellationToken);
        using var timeout = Timeout(answerTimeout, cancellationToken);
        while (true)
        {
            var packet = await ReceiveAsync(timeout.Token, timeout.Token, cancellationToken);
            if (packet.Type != AuthResponse)
            {
                continue; // srcds' empty response before the real answer
            }

            if (packet.Id == -1)
            {
                throw new RconException("The RCON password was refused.") { AuthenticationFailed = true };
            }

            return;
        }
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
            if (_closed || PeerClosed())
            {
                throw new RconException("The RCON connection was closed.") { ConnectionLost = true, NotSent = true };
            }

            await SendAsync(_nextId++, ExecCommand, command, cancellationToken);
            // The whole answer gets twice the time its first packet may take, and 1 MiB.
            using var deadline = Timeout(answerTimeout * 2, cancellationToken);
            var answer = new StringBuilder();
            using (var first = Timeout(answerTimeout, deadline.Token))
            {
                answer.Append((await ReceiveAsync(first.Token, first.Token, cancellationToken)).Body);
            }

            while (answer.Length < MaxAnswerLength)
            {
                // The idle wait ends the answer only between packets: once a packet started, the rest of it is awaited.
                using var idle = Timeout(answerIdle, deadline.Token);
                try
                {
                    answer.Append((await ReceiveAsync(idle.Token, deadline.Token, cancellationToken, idleEndsAnswer: true)).Body);
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

            // A game that keeps answering (a plugin flooding its output): what came so far, and a session no one reads from.
            await CloseAsync();
            return answer.Length > MaxAnswerLength ? answer.ToString(0, MaxAnswerLength) : answer.ToString();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _lock.Dispose();
    }

    private async Task CloseAsync()
    {
        _closed = true;
        await _stream.DisposeAsync();
        client.Dispose();
    }

    /// <summary>
    /// Whether the game closed the connection while it was idle: readable with nothing to read is its end. Without this
    /// check the command would be written into the closed connection, and its loss only seen once it was sent.
    /// </summary>
    private bool PeerClosed()
    {
        try
        {
            return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static CancellationTokenSource Timeout(TimeSpan after, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(after);
        return source;
    }

    private async Task SendAsync(int id, int type, string body, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetBytes(body);
        var packet = new byte[4 + 4 + 4 + text.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        text.CopyTo(packet.AsSpan(12));
        try
        {
            await _stream.WriteAsync(packet, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new RconException("The RCON connection was closed.", ex) { ConnectionLost = true };
        }
    }

    /// <summary>One packet: <paramref name="start"/> limits the wait for its first byte, <paramref name="rest"/> the rest of it.</summary>
    private async Task<(int Id, int Type, string Body)> ReceiveAsync(CancellationToken start, CancellationToken rest, CancellationToken cancellationToken,
        bool idleEndsAnswer = false)
    {
        try
        {
            var header = new byte[4];
            await _stream.ReadExactlyAsync(header.AsMemory(0, 1), start);
            await _stream.ReadExactlyAsync(header.AsMemory(1), rest);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size is < 10 or > MaxPacketSize)
            {
                throw new RconException($"Invalid RCON packet size {size}.");
            }

            var packet = new byte[size];
            await _stream.ReadExactlyAsync(packet, rest);
            var id = BinaryPrimitives.ReadInt32LittleEndian(packet);
            var type = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));
            var body = packet.AsSpan(8, size - 10);
            var end = body.IndexOf((byte)0);
            return (id, type, Encoding.UTF8.GetString(end < 0 ? body : body[..end]));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (idleEndsAnswer)
            {
                throw;
            }

            throw new RconException("The game did not answer over RCON in time.");
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            throw new RconException("The RCON connection was closed.", ex) { ConnectionLost = true };
        }
    }
}
