using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Sftp;

/// <summary>
/// The panel's SFTP server (Phase 17, docs/guides/sftp.md): SSH on its own port (2022 by default), one listener for every
/// node, with <c>Microsoft.DevTunnels.Ssh</c> for the transport and sign-in and Pulsar's own SFTP version 3 handler. Only
/// modern algorithms are offered (ECDH key exchange, an ECDSA P-256 host key, AES-GCM or AES-CTR with encrypt-then-MAC);
/// only <c>session</c> channels with the <c>sftp</c> subsystem are accepted: no shell, exec, terminal, environment, X11,
/// agent or port forwarding. Started after the panel's HTTP endpoints, and again when an administrator turns it on or off
/// or changes its port (<see cref="Reload"/>), which ends every connection.
/// </summary>
internal sealed partial class SftpHost(
    IOptions<SftpOptions> options,
    IServiceScopeFactory scopes,
    SftpHostKey hostKey,
    SftpConnections connections,
    SftpAuthenticator authenticator,
    IServer server,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<SftpHost> logger) : BackgroundService
{
    private static readonly TimeSpan s_retry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_refusalLogInterval = TimeSpan.FromMinutes(1);

    // The library's own tracing logs a stack trace for every dropped connection (spike 0003): off.
    private static readonly TraceSource s_trace = new("Pulsar.Sftp", SourceLevels.Off);

    private readonly SftpOptions _options = options.Value;
    private readonly Channel<bool> _reload = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _refusalLock = new();
    private volatile int _port;
    private long _refusedSince;
    private int _refused;

    /// <summary>The port it listens on, or null while it does not.</summary>
    public int? ListeningPort => _port is > 0 and var port ? port : null;

    /// <summary>Settings changed: listen again as they say (every connection ends).</summary>
    public void Reload() => _reload.Writer.TryWrite(true);

    /// <summary>The SSH settings: only what the spike found every client to need, modern algorithms only.</summary>
    internal static SshSessionConfiguration CreateConfiguration()
    {
        var configuration = new SshSessionConfiguration(useSecurity: true, enableCompression: false, enableReconnect: false);
        Keep(configuration.KeyExchangeAlgorithms, "ecdh-sha2-nistp384", "ecdh-sha2-nistp256");
        Keep(configuration.PublicKeyAlgorithms, "ecdsa-sha2-nistp256");
        Keep(configuration.EncryptionAlgorithms, "aes256-gcm@openssh.com", "aes256-ctr");
        Keep(configuration.HmacAlgorithms, "hmac-sha2-512-etm@openssh.com", "hmac-sha2-256-etm@openssh.com");
        configuration.AuthenticationMethods.Clear();
        configuration.AuthenticationMethods.Add(AuthenticationMethods.KeyboardInteractive);
        configuration.AuthenticationMethods.Add(AuthenticationMethods.Password);
        configuration.ProtocolExtensions.Remove(SshProtocolExtensionNames.OpenChannelRequest);
        configuration.ProtocolExtensions.Remove(SshProtocolExtensionNames.SessionReconnect);
        configuration.ProtocolExtensions.Remove(SshProtocolExtensionNames.SessionLatency);

        // "none" counts as one; the password checks are bounded apart (SftpOptions.MaxPasswordAttemptsPerConnection).
        configuration.MaxClientAuthenticationAttempts = 10;
        return configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Listen || !await StartedAsync(stoppingToken))
        {
            return;
        }

        var sweep = TimeSpan.FromTicks(Math.Clamp(Math.Min(_options.AuthenticationTimeout.Ticks, _options.IdleTimeout.Ticks) / 4,
            TimeSpan.FromMilliseconds(100).Ticks, TimeSpan.FromSeconds(5).Ticks));
        using var sweeper = clock.CreateTimer(_ => connections.Sweep(), null, sweep, sweep);
        while (!stoppingToken.IsCancellationRequested)
        {
            List<TcpListener> listeners = [];
            List<Task> accepting = [];
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var retry = false;
            try
            {
                var (enabled, port) = await ReadSettingsAsync(stoppingToken);
                if (enabled)
                {
                    var key = await hostKey.GetAsync(stoppingToken);
                    listeners = Listen(port);
                    var bound = ((IPEndPoint)listeners[0].LocalEndpoint).Port;
                    _port = bound;
                    accepting = [.. listeners.Select(l => AcceptAsync(l, key, stop.Token))];
                    var endpoints = string.Join(", ", listeners.Select(l => l.LocalEndpoint));
                    LogListening(logger, endpoints, key.Fingerprint);
                }
                else
                {
                    LogDisabled(logger);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                LogFailed(logger, ex);
                retry = true;
            }

            try
            {
                if (retry)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(s_retry);
                    try
                    {
                        await _reload.Reader.ReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Time to try again.
                    }
                }
                else
                {
                    await _reload.Reader.ReadAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The panel stops.
            }
            finally
            {
                _port = 0;
                await stop.CancelAsync();
                foreach (var listener in listeners)
                {
                    listener.Stop();
                }

                await Task.WhenAll(accepting);
                connections.CloseEverything(stoppingToken.IsCancellationRequested ? "The panel is stopping." : "The SFTP settings changed.");
            }
        }
    }

    private static void Keep<T>(ICollection<T?> algorithms, params string[] names)
        where T : SshAlgorithm
    {
        foreach (var algorithm in algorithms.ToList())
        {
            if (algorithm is null || !names.Contains(algorithm.Name, StringComparer.Ordinal))
            {
                algorithms.Remove(algorithm);
            }
        }

        if (algorithms.Count == 0)
        {
            throw new InvalidOperationException($"The SSH library offers none of {string.Join(", ", names)}.");
        }
    }

    private async Task<bool> StartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<(bool Enabled, int Port)> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().SftpSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return (stored?.Enabled ?? true, _options.PortOverride ?? stored?.Port ?? SftpSettings.DefaultPort);
    }

    private List<TcpListener> Listen(int port)
    {
        List<TcpListener> listeners = [];
        try
        {
            foreach (var address in BindAddresses())
            {
                var listener = new TcpListener(address, port);
                if (address.Equals(IPAddress.IPv6Any))
                {
                    listener.Server.DualMode = true;
                }

                if (OperatingSystem.IsWindows())
                {
                    // No other process may bind the same port to take the connections.
                    listener.ExclusiveAddressUse = true;
                }

                listener.Start(backlog: 64);
                listeners.Add(listener);

                // Port 0 (tests): the other addresses take the port the first one got.
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            return listeners;
        }
        catch
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }

            throw;
        }
    }

    /// <summary>
    /// <see cref="SftpOptions.BindAddress"/>, or as the panel's HTTP endpoints listen: every address when one of them does
    /// (or names a host), else the addresses they name, the loopback for <c>localhost</c>.
    /// </summary>
    private IReadOnlyList<IPAddress> BindAddresses()
    {
        var any = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        if (_options.BindAddress is { } configured)
        {
            return [configured == "*" ? any : IPAddress.Parse(configured)];
        }

        var addresses = new List<IPAddress>();
        foreach (var url in server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
        {
            string host;
            try
            {
                host = BindingAddress.Parse(url).Host.Trim('[', ']');
            }
            catch (FormatException)
            {
                continue;
            }

            if (host is "*" or "+" || (IPAddress.TryParse(host, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))))
            {
                return [any];
            }

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                addresses.Add(IPAddress.Loopback);
                if (Socket.OSSupportsIPv6)
                {
                    addresses.Add(IPAddress.IPv6Loopback);
                }
            }
            else if (ip is not null)
            {
                addresses.Add(ip);
            }
            else
            {
                // A host name: Kestrel listens everywhere for it.
                return [any];
            }
        }

        return addresses.Count == 0 ? [IPAddress.Loopback] : [.. addresses.Distinct()];
    }

    private async Task AcceptAsync(TcpListener listener, SftpHostKey.Loaded key, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(cancellationToken);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                LogAcceptFailed(logger, ex.SocketErrorCode);
                continue;
            }

            _ = Task.Run(() => ServeAsync(socket, key), CancellationToken.None);
        }
    }

    private async Task ServeAsync(Socket socket, SftpHostKey.Loaded key)
    {
        var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote is { IsIPv4MappedToIPv6: true })
        {
            remote = remote.MapToIPv4();
        }

        var connection = connections.TryAdmit(remote);
        if (connection is null)
        {
            socket.Dispose();
            Refused();
            return;
        }

        SshServerSession? session = null;
        try
        {
            socket.NoDelay = true;
            await using var network = new NetworkStream(socket, ownsSocket: true);
            session = new SshServerSession(CreateConfiguration(), s_trace) { Credentials = new SshServerCredentials(key.Pair) };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Closed += (_, _) => closed.TrySetResult();
            session.Authenticating += (_, e) => authenticator.Handle(connection, e);
            session.Request += (_, e) => e.IsAuthorized = false; // forwarding and every other global request
            session.ChannelOpening += (_, e) => OnChannelOpening(connection, e);
            connection.Session = session;
            if (connection.IsClosing)
            {
                return;
            }

            await session.ConnectAsync(network, connection.Ended);
            connection.SetClientVersion(session.RemoteVersion?.ToString());
            await closed.Task.WaitAsync(connection.Ended);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A client that went away, broke the protocol or was closed by a limit: nothing to keep.
            LogConnectionEnded(logger, connection.CorrelationId, ex);
        }
        finally
        {
            connection.Close(SshDisconnectReason.ByApplication, "Goodbye.");
            connections.Remove(connection);
            try
            {
                session?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogConnectionEnded(logger, connection.CorrelationId, ex);
            }

            connection.Dispose();
        }
    }

    private void OnChannelOpening(SftpConnection connection, SshChannelOpeningEventArgs e)
    {
        if (connection.Identity is null || !e.IsRemoteRequest || e.Request.ChannelType != SshChannel.SessionChannelType || e.Channel is null)
        {
            // Forwarding (direct-tcpip, x11, agent…) and anything else: OpenSSH then ends the connection, which is fine.
            e.FailureReason = SshChannelOpenFailureReason.AdministrativelyProhibited;
            e.FailureDescription = "Only SFTP is available.";
            return;
        }

        var channel = e.Channel;
        var started = false;
        channel.Request += (_, request) =>
        {
            request.IsAuthorized = false;
            if (started || request.RequestType != "subsystem" || connection.IsClosing || connection.Subsystems >= 2)
            {
                return;
            }

            string? name;
            try
            {
                name = request.Request.ConvertTo<SubsystemRequestMessage>().Subsystem;
            }
            catch (Exception ex) when (ex is SshConnectionException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                return;
            }

            if (name != "sftp")
            {
                return;
            }

            // The stream exists before the reply, so no SFTP packet arrives before it can be read (spike 0003).
            started = true;
            connection.Subsystems++;
            var stream = new SshStream(channel);
            request.IsAuthorized = true;
            _ = Task.Run(() => RunSftpAsync(connection, channel, stream), CancellationToken.None);
        };
    }

    private async Task RunSftpAsync(SftpConnection connection, SshChannel channel, SshStream stream)
    {
        var files = new SftpFileSystem(connection, scopes, clock);
        var session = new SftpSession(stream, connection, files, _options, clock, logger);
        try
        {
            await session.RunAsync(connection.Ended);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogConnectionEnded(logger, connection.CorrelationId, ex);
        }
        finally
        {
            await session.DisposeAsync();
            try
            {
                await channel.CloseAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The connection is gone already.
            }

            await stream.DisposeAsync();
        }
    }

    // One warning a minute at most, with how many connections a limit closed.
    private void Refused()
    {
        int count;
        lock (_refusalLock)
        {
            _refused++;
            if (_refusedSince != 0 && clock.GetElapsedTime(_refusedSince) < s_refusalLogInterval)
            {
                return;
            }

            count = _refused;
            _refused = 0;
            _refusedSince = clock.GetTimestamp();
        }

        LogRefused(logger, count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SFTP listening on {Endpoints}, host key {Fingerprint}")]
    private static partial void LogListening(ILogger logger, string endpoints, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "SFTP is turned off in the panel's settings")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The SFTP server could not start; it tries again in 30 seconds or when its settings change")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SFTP accept failed: {Error}")]
    private static partial void LogAcceptFailed(ILogger logger, SocketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SFTP connection {CorrelationId} ended")]
    private static partial void LogConnectionEnded(ILogger logger, string correlationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SFTP closed {Count} connection(s) on arrival: a connection limit was reached (Sftp:Max…)")]
    private static partial void LogRefused(ILogger logger, int count);
}

/// <summary>The "subsystem" channel request (RFC 4254, 6.5): the SSH library has no message type for it.</summary>
internal sealed class SubsystemRequestMessage : ChannelRequestMessage
{
    public SubsystemRequestMessage()
    {
        RequestType = "subsystem";
    }

    public string? Subsystem { get; set; }

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Subsystem = reader.ReadString(Encoding.UTF8);
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Subsystem ?? string.Empty, Encoding.UTF8);
    }
}
