using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>Opens remote console sessions to the games the agent runs (<c>logfile+rcon</c> consoles).</summary>
public interface IRconConnector
{
    /// <exception cref="RconException">The game refused the connection or the password, or did not answer.</exception>
    Task<IRconSession> ConnectAsync(RconEndpoint endpoint, CancellationToken cancellationToken);
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
}

/// <summary>
/// The Source RCON protocol (developer.valvesoftware.com/wiki/Source_RCON_Protocol) as srcds and CS2 speak it (spike
/// 0001): little-endian packets <c>size, id, type, body\0, \0</c>. Authentication is answered with one or two packets
/// (srcds sends an empty response first, CS2 does not); a command's answer is one or more response packets, read until
/// the connection has been idle for a moment (CS2 garbles the usual empty "end marker" request).
/// </summary>
public sealed class SourceRconConnector : IRconConnector
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long after the last packet an answer is considered complete.</summary>
    public TimeSpan AnswerIdle { get; init; } = TimeSpan.FromMilliseconds(250);

    public async Task<IRconSession> ConnectAsync(RconEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
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

    private readonly NetworkStream _stream = client.GetStream();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId = 1;

    public async Task AuthenticateAsync(string password, CancellationToken cancellationToken)
    {
        var id = _nextId++;
        await SendAsync(id, Auth, password, cancellationToken);
        using var timeout = Timeout(answerTimeout, cancellationToken);
        while (true)
        {
            var packet = await ReceiveAsync(timeout.Token, cancellationToken);
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
            await SendAsync(_nextId++, ExecCommand, command, cancellationToken);
            var answer = new StringBuilder();
            using (var first = Timeout(answerTimeout, cancellationToken))
            {
                answer.Append((await ReceiveAsync(first.Token, cancellationToken)).Body);
            }

            while (true)
            {
                using var idle = Timeout(answerIdle, cancellationToken);
                try
                {
                    answer.Append((await ReceiveAsync(idle.Token, cancellationToken, idleEndsAnswer: true)).Body);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return answer.ToString();
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        client.Dispose();
        _lock.Dispose();
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

    private async Task<(int Id, int Type, string Body)> ReceiveAsync(CancellationToken timeout, CancellationToken cancellationToken, bool idleEndsAnswer = false)
    {
        try
        {
            var header = new byte[4];
            await _stream.ReadExactlyAsync(header, timeout);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size is < 10 or > MaxPacketSize)
            {
                throw new RconException($"Invalid RCON packet size {size}.");
            }

            var packet = new byte[size];
            await _stream.ReadExactlyAsync(packet, timeout);
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
