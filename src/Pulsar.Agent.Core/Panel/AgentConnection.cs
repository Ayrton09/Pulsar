using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
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

    /// <summary>Age at which the node key is replaced automatically (docs/architecture/03: 90 days).</summary>
    public TimeSpan KeyRotationInterval { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Wait before trying again after a rotation that failed.</summary>
    public TimeSpan RotationRetryDelay { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Keeps this node connected to its panel (docs/architecture/03): authenticate by challenge, open the hub over
/// WebSockets, say Hello, send node metrics and execute verified commands. A lost connection is retried with
/// exponential backoff and jitter; a session about to expire is replaced by a new one before it ends. It also owns the
/// node's identity: key rotation (every 90 days or when the panel asks) and updates of the pinned panel keys.
/// </summary>
public sealed partial class AgentConnection(
    AgentConfigStore configs,
    INodeKeyStore keys,
    INodeSystem system,
    PanelTransport transport,
    PanelAuthenticator authenticator,
    CredentialRotationClient rotation,
    CommandDispatcher dispatcher,
    AgentConnectionOptions options,
    TimeProvider clock,
    IHostApplicationLifetime lifetime,
    ILoggerFactory loggerFactory,
    PanelChannel? channel = null,
    ServerHost? servers = null) : BackgroundService, IAgentControl
{
    /// <summary>Enough failures for the backoff to sit at its maximum.</summary>
    private const int MaxFailures = 30;

    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentConnection>();
    private readonly PanelChannel _channel = channel ?? new PanelChannel();
    private readonly Lock _configLock = new();
    private AgentConfig _config = null!;
    private INodeKey _key = null!;
    private TaskCompletionSource _rotationRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _nextRotationAttempt = DateTimeOffset.MinValue;

    private enum SessionEnd
    {
        Closed,
        Renew,
        Rotate,
    }

    /// <summary>The live session, if any (for tests and diagnostics).</summary>
    public Guid? CurrentSessionId { get; private set; }

    /// <summary>The configuration in use (it changes with key rotations and panel key updates).</summary>
    public AgentConfig? Config => _config;

    public bool RequestCredentialRotation()
    {
        lock (_configLock)
        {
            if (_rotationRequested.Task.IsCompleted)
            {
                return false;
            }

            _nextRotationAttempt = DateTimeOffset.MinValue;
            _rotationRequested.TrySetResult();
            return true;
        }
    }

    public bool ReplacePanelKeys(IReadOnlyList<PanelKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_configLock)
        {
            var current = _config.PanelKeys.Select(k => k.KeyId).Order(StringComparer.Ordinal);
            if (current.SequenceEqual(keys.Select(k => k.KeyId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                return false;
            }

            SaveConfig(_config with { PanelKeys = [.. keys] });
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = configs.Load();
        if (config is null)
        {
            LogNotEnrolled(configs.Paths.ConfigFile);
            Stop();
            return;
        }

        try
        {
            _key = keys.Open(config.Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogKeyUnavailable(ex, config.Key.Name);
            Stop();
            return;
        }

        _config = config;
        if (servers is not null)
        {
            // The first Hello reports every server.
            await servers.Loaded.WaitAsync(stoppingToken);
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
                    var next = await ConnectAsync(stoppingToken);
                    if (current is not null)
                    {
                        // The panel already closed it when the new session said Hello.
                        await current.DisposeAsync();
                    }

                    current = next;
                    CurrentSessionId = current.SessionId;
                    failures = 0;

                    SessionEnd end;
                    do
                    {
                        end = RotationDue() ? SessionEnd.Rotate : await current.WaitAsync(_rotationRequested.Task, stoppingToken);
                    }
                    while (end == SessionEnd.Rotate && !await RotateAsync(current, stoppingToken)); // failed: keep this session

                    if (end == SessionEnd.Rotate)
                    {
                        // The next connection authenticates with the new key, which completes the rotation.
                        continue;
                    }

                    if (end == SessionEnd.Renew)
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

            _key.Dispose();
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

    private void SaveConfig(AgentConfig config)
    {
        configs.Save(config);
        _config = config;
    }

    private bool RotationDue()
    {
        var now = clock.GetUtcNow();
        return now >= _nextRotationAttempt
            && (_rotationRequested.Task.IsCompleted || now - _config.CurrentKeyCreatedAt >= options.KeyRotationInterval);
    }

    /// <summary>
    /// Step 1 of a rotation: a new key, registered with the panel as a pending credential and saved as pending here
    /// before anything else uses it. Step 2 is the next authentication (<see cref="AuthenticateAsync"/>).
    /// </summary>
    private async Task<bool> RotateAsync(Session session, CancellationToken cancellationToken)
    {
        lock (_configLock)
        {
            _rotationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var config = _config;
        if (config.PendingRotation is { } stale)
        {
            // An earlier rotation that never completed: the panel drops its pending credential too.
            DeleteKey(stale.Key);
        }

        var newKey = keys.Create($"PulsarAgent-{Guid.NewGuid():N}", config.Key.Scope);
        try
        {
            var credentialId = await rotation.RegisterAsync(config, session.AccessToken, session.SessionId, newKey, cancellationToken);
            lock (_configLock)
            {
                SaveConfig(_config with { PendingRotation = new PendingRotation(newKey.Reference, credentialId, clock.GetUtcNow()) });
            }

            LogRotationStarted(credentialId, newKey.Reference.Storage);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            DeleteKey(newKey.Reference);
            lock (_configLock)
            {
                if (_config.PendingRotation is not null)
                {
                    SaveConfig(_config with { PendingRotation = null });
                }
            }

            _nextRotationAttempt = clock.GetUtcNow() + options.RotationRetryDelay;
            LogRotationFailed(ex.Message, _nextRotationAttempt);
            if (!IsExpected(ex))
            {
                throw;
            }

            return false;
        }
        finally
        {
            newKey.Dispose();
        }
    }

    /// <summary>
    /// Authenticates with the pending key first when there is one: success completes the rotation (the panel
    /// activates it; the agent switches to it and deletes the old key), refusal abandons it, and a network error leaves
    /// it for the next attempt. A crash anywhere in between leaves one of the two keys working.
    /// </summary>
    private async Task<TokenResponse> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (_config.PendingRotation is { } pending)
        {
            INodeKey? newKey = null;
            try
            {
                newKey = keys.Open(pending.Key);
                var token = await authenticator.AuthenticateAsync(_config with { CredentialId = pending.CredentialId }, newKey, cancellationToken);
                CompleteRotation(pending, newKey);
                return token;
            }
            catch (Exception ex) when (ex is AgentRejectedException or CryptographicException)
            {
                newKey?.Dispose();
                LogPendingKeyAbandoned(pending.CredentialId, ex.Message);
                DeleteKey(pending.Key);
                lock (_configLock)
                {
                    SaveConfig(_config with { PendingRotation = null });
                }
            }
            catch
            {
                newKey?.Dispose();
                throw;
            }
        }

        return await authenticator.AuthenticateAsync(_config, _key, cancellationToken);
    }

    private void CompleteRotation(PendingRotation pending, INodeKey newKey)
    {
        var old = _key;
        lock (_configLock)
        {
            SaveConfig(_config with { Key = pending.Key, CredentialId = pending.CredentialId, KeyCreatedAt = pending.CreatedAt, PendingRotation = null });
            _key = newKey;
        }

        DeleteKey(old.Reference);
        old.Dispose();
        LogRotated(pending.CredentialId, pending.Key.Storage);
    }

    private void DeleteKey(NodeKeyReference reference)
    {
        try
        {
            keys.Delete(reference);
        }
        catch (CryptographicException ex)
        {
            // An orphaned key is harmless: the panel no longer accepts it.
            LogKeyNotDeleted(reference.Name, ex.Message);
        }
    }

    private async Task<Session> ConnectAsync(CancellationToken cancellationToken)
    {
        var token = await AuthenticateAsync(cancellationToken);
        var config = _config;
        var verifier = new CommandVerifier(config.NodeId, token.SessionId, () => _config.PanelKeys, dispatcher.SupportedCommands, clock);

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

        var session = new Session(connection, token.SessionId, token.AccessToken, system, options, clock, _logger, _channel);
        try
        {
            connection.On<SignedMessage, CommandAck>(nameof(IAgentClient.Execute), message => dispatcher.ExecuteAsync(verifier, message, this));
            await connection.StartAsync(cancellationToken);

            var hello = await connection.InvokeAsync<HelloAccepted>(nameof(IAgentHub.Hello),
                new AgentHello(AgentInfo.Version, AgentProtocol.CurrentVersion, [.. dispatcher.SupportedCommands],
                    system.GetInventory(config.ServersRootOrDefault(configs.Paths)), [.. config.PanelKeys.Select(k => k.KeyId)],
                    servers?.Snapshots() ?? [], servers?.OperationsInFlight() ?? []),
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
            await _channel.AttachAsync(connection, cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Rotating the node key: new credential {CredentialId} ({Storage}) registered, authenticating with it")]
    private partial void LogRotationStarted(Guid credentialId, string storage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node key rotated: now using credential {CredentialId} ({Storage}); the previous key is deleted")]
    private partial void LogRotated(Guid credentialId, string storage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node key rotation failed ({Reason}); the current key stays in use. Next attempt after {NextAttempt}")]
    private partial void LogRotationFailed(string reason, DateTimeOffset nextAttempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoning the pending key of credential {CredentialId} ({Reason}); the current key stays in use")]
    private partial void LogPendingKeyAbandoned(Guid credentialId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the node key {KeyName}: {Reason}")]
    private partial void LogKeyNotDeleted(string keyName, string reason);

    /// <summary>One authenticated hub connection and its metrics loop.</summary>
    private sealed partial class Session(
        HubConnection connection,
        Guid sessionId,
        string accessToken,
        INodeSystem system,
        AgentConnectionOptions options,
        TimeProvider clock,
        ILogger logger,
        PanelChannel channel) : IAsyncDisposable
    {
        private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private DateTimeOffset _renewAt;
        private Task _metrics = Task.CompletedTask;

        public Guid SessionId { get; } = sessionId;

        /// <summary>The session token, for calls the agent makes within this session (key rotation).</summary>
        public string AccessToken { get; } = accessToken;

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

        /// <summary>Until the connection closes, the session is due for renewal, or a key rotation is requested.</summary>
        public async Task<SessionEnd> WaitAsync(Task rotationRequested, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var wait = _renewAt - clock.GetUtcNow();
            var renewal = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, clock, linked.Token);
            var first = await Task.WhenAny(_closed.Task, renewal, rotationRequested);
            await linked.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed.Task.IsCompleted)
            {
                return SessionEnd.Closed;
            }

            return first == rotationRequested ? SessionEnd.Rotate : SessionEnd.Renew;
        }

        public async ValueTask DisposeAsync()
        {
            channel.Detach(connection);
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
