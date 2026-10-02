using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Panel;

public sealed class AgentConnectionOptions
{
    /// <summary>Node metrics period (docs/architecture/03: 15 s).</summary>
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long before the session expires a new one is opened, so the hand-over leaves no gap.</summary>
    public TimeSpan RenewalLead { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound of the reconnect backoff.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Clock difference with the panel worth a warning (command expiry depends on it).</summary>
    public TimeSpan ClockSkewWarning { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Keeps this node connected to its panel (docs/architecture/03): authenticate by challenge, open the hub over
/// WebSockets, say Hello, send node metrics and execute verified commands. A lost connection is retried with
/// exponential backoff and jitter; a session about to expire is replaced by a new one before it ends.
/// </summary>
public sealed partial class AgentConnection(
    AgentConfigStore configs,
    INodeKeyStore keys,
    INodeSystem system,
    PanelTransport transport,
    PanelAuthenticator authenticator,
    CommandDispatcher dispatcher,
    AgentConnectionOptions options,
    TimeProvider clock,
    IHostApplicationLifetime lifetime,
    ILoggerFactory loggerFactory) : BackgroundService
{
    /// <summary>Enough failures for the backoff to sit at its maximum.</summary>
    private const int MaxFailures = 30;

    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentConnection>();

    /// <summary>The live session, if any (for tests and diagnostics).</summary>
    public Guid? CurrentSessionId { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = configs.Load();
        if (config is null)
        {
            LogNotEnrolled(configs.Paths.ConfigFile);
            Stop();
            return;
        }

        INodeKey key;
        try
        {
            key = keys.Open(config.Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogKeyUnavailable(ex, config.Key.Name);
            Stop();
            return;
        }

        var origin = PanelAddress.Origin(config.Panel);
        LogStarting(AgentInfo.Version, config.NodeId, origin);
        Session? current = null;
        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var next = await ConnectAsync(config, key, stoppingToken);
                    if (current is not null)
                    {
                        // The panel already closed it when the new session said Hello.
                        await current.DisposeAsync();
                    }

                    current = next;
                    CurrentSessionId = current.SessionId;
                    failures = 0;
                    if (await current.WaitForRenewalAsync(stoppingToken))
                    {
                        LogRenewing(current.SessionId);
                        continue;
                    }

                    LogDisconnected(current.CloseReason?.Message ?? "closed by the panel");
                    CurrentSessionId = null;
                    await current.DisposeAsync();
                    current = null;
                    failures = 1;
                }
                catch (AgentRejectedException ex)
                {
                    LogRejected(ex.Message);
                    failures = MaxFailures;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Network failures are routine while the panel or the link is down: one line each, no stack trace.
                    if (IsExpected(ex))
                    {
                        LogConnectFailed(ex.Message);
                    }
                    else
                    {
                        LogConnectFailedUnexpectedly(ex);
                    }

                    failures = Math.Min(failures + 1, MaxFailures);
                }

                var delay = Backoff(failures, options.MaxBackoff);
                LogRetrying(delay);
                await Task.Delay(delay, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            if (current is not null)
            {
                await current.DisposeAsync();
            }

            key.Dispose();
        }
    }

    /// <summary>Exponential (1, 2, 4… s) up to <see cref="AgentConnectionOptions.MaxBackoff"/>, randomized to 50–100 %.</summary>
    internal static TimeSpan Backoff(int failures, TimeSpan max)
    {
        var exponential = failures >= MaxFailures ? max : TimeSpan.FromSeconds(Math.Pow(2, Math.Max(failures, 1) - 1));
        var capped = exponential < max ? exponential : max;
        return capped * (0.5 + (Random.Shared.NextDouble() / 2));
    }

    private static bool IsExpected(Exception exception) => exception is AgentException or HttpRequestException or HubException
        or System.Net.WebSockets.WebSocketException or IOException or TimeoutException or TaskCanceledException;

    private void Stop()
    {
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    private async Task<Session> ConnectAsync(AgentConfig config, INodeKey key, CancellationToken cancellationToken)
    {
        var token = await authenticator.AuthenticateAsync(config, key, cancellationToken);
        var verifier = new CommandVerifier(config.NodeId, token.SessionId, config.PanelKeys, dispatcher.SupportedCommands, clock);

        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(config.Panel, AgentProtocol.HubPath), HttpTransportType.WebSockets, o =>
            {
                o.SkipNegotiation = true;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token.AccessToken);
                transport.ConfigureHub(o);
            })
            .AddJsonProtocol(o => ProtocolJson.Configure(o.PayloadSerializerOptions));
        builder.Services.AddSingleton(loggerFactory);
        var connection = builder.Build();

        var session = new Session(connection, token.SessionId, system, options, clock, _logger);
        try
        {
            connection.On<SignedMessage, CommandAck>(nameof(IAgentClient.Execute), message => dispatcher.Execute(verifier, message));
            await connection.StartAsync(cancellationToken);

            var hello = await connection.InvokeAsync<HelloAccepted>(nameof(IAgentHub.Hello),
                new AgentHello(AgentInfo.Version, AgentProtocol.CurrentVersion, [.. dispatcher.SupportedCommands], system.GetInventory(config.ServersRoot)),
                cancellationToken);

            var now = clock.GetUtcNow();
            verifier.PanelClockOffset = hello.ServerTime - now;
            if (verifier.PanelClockOffset.Duration() > options.ClockSkewWarning)
            {
                LogClockSkew(verifier.PanelClockOffset);
            }

            var lifetimeLeft = token.ExpiresAt - hello.ServerTime;
            var lead = options.RenewalLead < lifetimeLeft / 2 ? options.RenewalLead : lifetimeLeft / 2;
            session.Started(now + lifetimeLeft - lead);
            LogConnected(token.SessionId, verifier.PanelClockOffset);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "This agent is not enrolled ({ConfigFile} not found). Run 'PulsarAgent enroll' first")]
    private partial void LogNotEnrolled(string configFile);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node key {KeyName} cannot be opened; enroll this node again")]
    private partial void LogKeyUnavailable(Exception exception, string keyName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pulsar Agent {Version} for node {NodeId}, panel {Panel}")]
    private partial void LogStarting(string version, Guid nodeId, string panel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the panel (session {SessionId}, panel clock offset {Offset})")]
    private partial void LogConnected(Guid sessionId, TimeSpan offset);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} expires soon; opening a new one")]
    private partial void LogRenewing(Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected from the panel: {Reason}")]
    private partial void LogDisconnected(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The panel refused this node: {Reason}")]
    private partial void LogRejected(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot connect to the panel: {Reason}")]
    private partial void LogConnectFailed(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while connecting to the panel")]
    private partial void LogConnectFailedUnexpectedly(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrying in {Delay}")]
    private partial void LogRetrying(TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "This machine's clock differs from the panel's by {Offset}; fix the time synchronization")]
    private partial void LogClockSkew(TimeSpan offset);

    /// <summary>One authenticated hub connection and its metrics loop.</summary>
    private sealed partial class Session(
        HubConnection connection,
        Guid sessionId,
        INodeSystem system,
        AgentConnectionOptions options,
        TimeProvider clock,
        ILogger logger) : IAsyncDisposable
    {
        private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private DateTimeOffset _renewAt;
        private Task _metrics = Task.CompletedTask;

        public Guid SessionId { get; } = sessionId;

        public Exception? CloseReason => _closed.Task.IsCompleted ? _closed.Task.Result : null;

        public void Started(DateTimeOffset renewAt)
        {
            _renewAt = renewAt;
            connection.Closed += error =>
            {
                _closed.TrySetResult(error);
                return Task.CompletedTask;
            };

            // Closed is only raised for connections that were open; one lost during Hello has already failed it.
            if (connection.State == HubConnectionState.Disconnected)
            {
                _closed.TrySetResult(null);
            }

            _metrics = SendMetricsAsync(_stop.Token);
        }

        /// <summary>True when it is time to renew; false when the connection closed first.</summary>
        public async Task<bool> WaitForRenewalAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var wait = _renewAt - clock.GetUtcNow();
            var renewal = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, clock, linked.Token);
            var first = await Task.WhenAny(_closed.Task, renewal);
            await linked.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return first == renewal && !_closed.Task.IsCompleted;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _metrics;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            await connection.DisposeAsync();
            _stop.Dispose();
        }

        private async Task SendMetricsAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(options.MetricsInterval, clock);
            do
            {
                try
                {
                    await connection.SendAsync(nameof(IAgentHub.NodeMetrics), system.SampleMetrics(), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The connection is going away; the reconnect loop takes it from here.
                    LogMetricsFailed(ex.Message);
                    return;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Metrics not sent: {Reason}")]
        private partial void LogMetricsFailed(string reason);
    }
}
