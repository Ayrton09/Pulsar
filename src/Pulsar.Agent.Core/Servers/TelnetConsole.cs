using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The remote consoles the agent speaks, by the manifest's protocol: Source RCON, or a telnet console
/// (<see cref="TelnetConsoleConnector"/>).
/// </summary>
/// <param name="peers">Who listens where, checked before anything is sent (docs/architecture/08, T18); null checks nothing.</param>
public sealed class RconConnectors(IRconConnector? source = null, IRconConnector? telnet = null, ILocalPeers? peers = null) : IRconConnector
{
    private readonly IRconConnector _source = source ?? new SourceRconConnector { Peers = peers };
    private readonly IRconConnector _telnet = telnet ?? new TelnetConsoleConnector { Peers = peers };

    public Task<IRconSession> ConnectAsync(RconEndpoint endpoint, ProcessIdentity owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Protocol switch
        {
            RconProtocol.Telnet => _telnet.ConnectAsync(endpoint, owner, cancellationToken),
            _ => _source.ConnectAsync(endpoint, owner, cancellationToken),
        };
    }
}

/// <summary>
/// A line console over telnet, as 7 Days to Die serves it (Phase 12 spike): a banner, a password prompt when the game
/// has a password, then commands one per line. The game also streams its whole log to every telnet client, without
/// marking where an answer ends: a reader drains the connection all the time (the game must never block writing to
/// it), and an answer is the lines that arrive after the command, without the log lines (<c>2026-10-05T12:14:43
/// 74.506 INF …</c>) the console already shows from the game's own output, until no answer line came for a moment.
/// </summary>
public sealed class TelnetConsoleConnector : IRconConnector
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long after the last text an answer is considered complete.</summary>
    public TimeSpan AnswerIdle { get; init; } = TimeSpan.FromMilliseconds(400);

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
                    throw new RconException($"Cannot connect to the telnet console at {endpoint.Address}:{endpoint.Port}.", ex);
                }
            }

            RconPeers.Verify(Peers, client.Client, endpoint, owner);
            var session = new TelnetConsoleSession(client, AnswerTimeout, AnswerIdle);
            await session.LogOnAsync(endpoint.Password, cancellationToken);
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed partial class TelnetConsoleSession : IRconSession
{
    // Kept while nobody reads it: enough for an answer, bounded however long the session stays idle.
    private const int MaxBuffered = 256 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan _answerIdle;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lock _bufferLock = new();
    private readonly StringBuilder _buffer = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reader;
    private readonly StringBuilder _line = new(); // the line being received
    private long _received; // total characters received (the buffer may have dropped its start)
    private long _answerLines; // lines received that are not the game's log
    private DateTimeOffset _lastAnswerLine = DateTimeOffset.MinValue;
    private volatile bool _closed;

    public TelnetConsoleSession(TcpClient client, TimeSpan answerTimeout, TimeSpan answerIdle)
    {
        _client = client;
        _stream = client.GetStream();
        _answerTimeout = answerTimeout;
        _answerIdle = answerIdle;
        _reader = Task.Run(ReadAsync);
    }

    /// <summary>Waits for the banner; answers the password prompt when the game asks for one.</summary>
    public async Task LogOnAsync(string password, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _answerTimeout;
        var sentPassword = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var text = Snapshot(0);
            if (text.Contains("Please enter password", StringComparison.OrdinalIgnoreCase) && !sentPassword)
            {
                Clear();
                await WriteLineAsync(password, cancellationToken);
                sentPassword = true;
                continue;
            }

            if (sentPassword && text.Contains("incorrect", StringComparison.OrdinalIgnoreCase))
            {
                throw new RconException("The telnet password was refused.") { AuthenticationFailed = true };
            }

            if ((sentPassword && text.Contains("Logon successful", StringComparison.OrdinalIgnoreCase))
                || (!sentPassword && text.Contains("Press 'help'", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (_closed)
            {
                throw new RconException("The telnet console closed the connection.") { ConnectionLost = true };
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new RconException("The telnet console did not greet in time.");
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
                // The reader saw the game close it: the command is not written into a closed connection.
                throw new RconException("The telnet console closed the connection.") { ConnectionLost = true, NotSent = true };
            }

            var (mark, lines) = Mark();
            await WriteLineAsync(command, cancellationToken);
            var started = DateTimeOffset.UtcNow;
            while (true)
            {
                await Task.Delay(50, cancellationToken);
                if (_closed)
                {
                    throw new RconException("The telnet console closed the connection.") { ConnectionLost = true };
                }

                var now = DateTimeOffset.UtcNow;
                bool idle;
                bool any;
                lock (_bufferLock)
                {
                    any = _answerLines > lines;
                    idle = now - _lastAnswerLine >= _answerIdle;
                }

                if ((any && idle) || now - started >= _answerTimeout)
                {
                    return Answer(Snapshot(mark));
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
        await _closing.CancelAsync();
        _client.Dispose();
        try
        {
            await _reader;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // Closing.
        }

        _closing.Dispose();
        _lock.Dispose();
    }

    /// <summary>The answer's own lines: the game's log lines and the telnet echo of the prompt are left out.</summary>
    internal static string Answer(string text)
    {
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Where(line => line.Trim().Length > 0 && !LogLine().IsMatch(line));
        return string.Join('\n', lines);
    }

    private async Task ReadAsync()
    {
        var bytes = new byte[16 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int read;
            while ((read = await _stream.ReadAsync(bytes, _closing.Token)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                lock (_bufferLock)
                {
                    _buffer.Append(chars, 0, count);
                    _received += count;
                    CountLines(chars.AsSpan(0, count));
                    if (_buffer.Length > MaxBuffered)
                    {
                        _buffer.Remove(0, _buffer.Length - MaxBuffered);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // The game or the agent closed the connection.
        }
        finally
        {
            _closed = true;
        }
    }

    private (long Received, long AnswerLines) Mark()
    {
        lock (_bufferLock)
        {
            return (_received, _answerLines);
        }
    }

    /// <summary>Counts the complete lines that are not the game's log (under the buffer's lock).</summary>
    private void CountLines(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c != '\n')
            {
                if (_line.Length < 4096)
                {
                    _line.Append(c);
                }

                continue;
            }

            var line = _line.ToString().TrimEnd('\r');
            _line.Clear();
            if (line.Trim().Length > 0 && !LogLine().IsMatch(line))
            {
                _answerLines++;
                _lastAnswerLine = DateTimeOffset.UtcNow;
            }
        }
    }

    private void Clear()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
        }
    }

    /// <summary>What arrived since <paramref name="mark"/> (as much of it as the buffer still holds).</summary>
    private string Snapshot(long mark)
    {
        lock (_bufferLock)
        {
            var start = _buffer.Length - (int)Math.Min(_buffer.Length, Math.Max(0, _received - mark));
            return _buffer.ToString(start, _buffer.Length - start);
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), cancellationToken);
        }
        catch (IOException ex)
        {
            throw new RconException("The telnet connection was closed.", ex) { ConnectionLost = true };
        }
    }

    // 7 Days to Die's log lines: "2026-10-05T12:14:43 74.506 INF ...".
    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d \d+\.\d+ [A-Z]{3} ", RegexOptions.CultureInvariant)]
    private static partial Regex LogLine();
}
