using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Commands;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
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

    /// <summary>
    /// How long signing in at the panel's agents' address may take before the agent answers that it cannot (the panel
    /// waits 10 s for the answer).
    /// </summary>
    public TimeSpan AgentsAddressTimeout { get; set; } = TimeSpan.FromSeconds(7);

    /// <summary>
    /// How long opening the hub, its Hello and what waited for a connection may take: a panel or proxy that keeps the
    /// connection alive but never answers must not hold the agent there.
    /// </summary>
    public TimeSpan SessionStartTimeout { get; set; } = PanelTransport.RequestTimeout;

    /// <summary>
    /// The least a session waits before it is renewed: one whose token is already due (a clock far off, a token issued
    /// about to expire) is not renewed in a loop.
    /// </summary>
    public TimeSpan MinimumRenewalDelay { get; set; } = TimeSpan.FromSeconds(5);
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
    ServerHost? servers = null,
    SteamLoginStore? steamLogins = null) : BackgroundService, IAgentControl
{
    /// <summary>Enough failures for the backoff to sit at its maximum.</summary>
    private const int MaxFailures = 30;

    /// <summary>Answers without the panel's words to the pending key, in a row, after which it is given up (the current key works).</summary>
    private const int MaxPendingKeyMisses = 3;

    /// <summary>How long a session must last for the reconnect backoff to start over.</summary>
    private static readonly TimeSpan s_stableSession = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentConnection>();
    private readonly PanelChannel _channel = channel ?? new PanelChannel();
    private readonly Lock _configLock = new();
    private AgentConfig _config = null!;
    private INodeKey _key = null!;
    private X509Certificate2? _certificate;
    private Uri? _sessionPanel;
    private X509Certificate2? _sessionCertificate;
    private Uri? _attemptPanel;
    private TaskCompletionSource _rotationRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _reconnectRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _nextRotationAttempt = DateTimeOffset.MinValue;
    private int _pendingKeyMisses;

    private enum SessionEnd
    {
        Closed,
        Renew,
        Move,
        Rotate,
    }

    /// <summary>The live session, if any (for tests and diagnostics).</summary>
    public Guid? CurrentSessionId { get; private set; }

    /// <summary>The address the live session was opened at: the configured one or its fallback (for tests and diagnostics).</summary>
    public Uri? CurrentPanel => _sessionPanel;

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
        _certificate = CreateCertificate(_key);
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
                    var connectedAt = clock.GetUtcNow();

                    SessionEnd end;
                    do
                    {
                        end = RotationDue() ? SessionEnd.Rotate : await current.WaitAsync(RotationWakeUp(stoppingToken), _reconnectRequested.Task, stoppingToken);
                    }
                    while (end == SessionEnd.Rotate && !await RotateAsync(current, stoppingToken)); // failed: keep this session

                    // A move asked for is carried out by whatever comes next (a renewal, a rotation, a reconnect).
                    ResetReconnectRequest();

                    if (end == SessionEnd.Rotate)
                    {
                        // The next connection authenticates with the new key, which completes the rotation.
                        failures = 0;
                        continue;
                    }

                    if (end == SessionEnd.Move)
                    {
                        failures = 0;
                        continue;
                    }

                    var lasted = clock.GetUtcNow() - connectedAt;
                    if (end == SessionEnd.Renew)
                    {
                        LogRenewing(current.SessionId);
                        if (lasted >= s_stableSession)
                        {
                            failures = 0;
                            continue;
                        }

                        // Due right after it opened (a token issued about to expire, a clock far off): the next one
                        // opens after the backoff, while this one goes on, rather than in a loop of sign-ins.
                        failures = Math.Min(failures + 1, MaxFailures);
                    }
                    else
                    {
                        LogDisconnected(current.CloseReason?.Message ?? "closed by the panel");
                        CurrentSessionId = null;
                        await current.DisposeAsync();
                        current = null;
                        failures = FailuresAfterSession(failures, lasted);
                    }
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
            _certificate?.Dispose();
        }
    }

    /// <summary>Exponential (1, 2, 4… s) up to <see cref="AgentConnectionOptions.MaxBackoff"/>, randomized to 50–100 %.</summary>
    internal static TimeSpan Backoff(int failures, TimeSpan max)
    {
        var exponential = failures >= MaxFailures ? max : TimeSpan.FromSeconds(Math.Pow(2, Math.Max(failures, 1) - 1));
        var capped = exponential < max ? exponential : max;
        return capped * (0.5 + (Random.Shared.NextDouble() / 2));
    }

    /// <summary>
    /// The failure count after a session closed: back to one once it lasted a minute, otherwise one more
    /// (a session closed right after its Hello, as two machines sharing one node identity do to each other, is no success).
    /// </summary>
    internal static int FailuresAfterSession(int failures, TimeSpan lasted) =>
        lasted >= s_stableSession ? 1 : Math.Min(failures + 1, MaxFailures);

    private static bool IsExpected(Exception exception) => exception is AgentException or HttpRequestException or HubException
        or System.Net.WebSockets.WebSocketException or IOException or TimeoutException or TaskCanceledException;

    private void ResetReconnectRequest()
    {
        lock (_configLock)
        {
            if (_reconnectRequested.Task.IsCompleted)
            {
                _reconnectRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

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
    /// What ends a session's wait for a rotation: the panel's request, or, while a request waits out the retry delay of a
    /// rotation that failed, the end of that delay.
    /// </summary>
    private Task RotationWakeUp(CancellationToken cancellationToken)
    {
        var requested = _rotationRequested.Task;
        var wait = _nextRotationAttempt - clock.GetUtcNow();
        return requested.IsCompleted && wait > TimeSpan.Zero ? Task.Delay(wait, clock, cancellationToken) : requested;
    }

    /// <summary>
    /// Step 1 of a rotation: a new key, registered with the panel as a pending credential and saved as pending here
    /// before anything else uses it. Step 2 is the next authentication (<see cref="AuthenticateAsync"/>). A rotation the
    /// panel asked for stays asked for until one registers: one that fails is tried again after the retry delay.
    /// </summary>
    private async Task<bool> RotateAsync(Session session, CancellationToken cancellationToken)
    {
        bool requested;
        lock (_configLock)
        {
            requested = _rotationRequested.Task.IsCompleted;
            _rotationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // An earlier rotation that never completed: the panel drops its pending credential when it registers this one.
        var config = _config;
        var stale = config.PendingRotation;
        INodeKey? newKey = null;
        var saved = false;
        try
        {
            newKey = keys.Create($"PulsarAgent-{Guid.NewGuid():N}", config.Key.Scope);
            var credentialId = await rotation.RegisterAsync(config, _sessionPanel ?? config.Panel, _sessionCertificate, session.AccessToken, session.SessionId,
                _key, newKey, cancellationToken);
            lock (_configLock)
            {
                SaveConfig(_config with { PendingRotation = new PendingRotation(newKey.Reference, credentialId, clock.GetUtcNow()) });
                saved = true;
            }

            // Only now, with the panel's signed answer: until it dropped the earlier pending credential, a sign-in with that
            // key held back on the way could still activate it.
            if (stale is not null)
            {
                DeleteKey(stale.Key);
            }

            LogRotationStarted(credentialId, newKey.Reference.Storage);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            lock (_configLock)
            {
                if (_config.PendingRotation != stale)
                {
                    SaveConfig(_config with { PendingRotation = stale });
                    saved = false;
                }
            }

            _nextRotationAttempt = clock.GetUtcNow() + options.RotationRetryDelay;
            if (requested)
            {
                lock (_configLock)
                {
                    _rotationRequested.TrySetResult();
                }
            }

            LogRotationFailed(ex.Message, _nextRotationAttempt);
            // A key this machine cannot create (its key storage refusing) is no reason to drop the session.
            if (newKey is not null && !IsExpected(ex))
            {
                throw;
            }

            return false;
        }
        finally
        {
            // Unless it is saved as pending, nothing will ever use the new key: also when the service stops meanwhile.
            if (newKey is not null)
            {
                if (!saved)
                {
                    DeleteKey(newKey.Reference);
                }

                newKey.Dispose();
            }
        }
    }

    /// <summary>
    /// Authenticates with the pending key first when there is one: success completes the rotation (the panel granted
    /// the session, so it activated the key; the agent switches to it and deletes the old key), and a network error leaves
    /// it for the next attempt. Every sign-in the agent uses is one the panel granted (<see cref="PanelAuthenticator"/>):
    /// an answer without the grant is no sign-in at all. A refusal, or a key that fails to sign, signs in with the current
    /// key: the pending key is abandoned only if that works, since only a sign-in of the current key the panel itself
    /// recorded proves it did not activate the pending one (a refusal forged after the panel took the request, a node
    /// disabled right after, a lost answer; and the panel refuses a pending key's sign-in held back since). With both
    /// refused, both are kept and tried again. A crash anywhere in between leaves one of the two keys working. An answer
    /// that is not the panel's (an older panel's bare 401 for a credential it dropped, or one without its grant) signs in
    /// with the current key for that attempt; after <see cref="MaxPendingKeyMisses"/> such answers in a row, while the
    /// current key works, the pending key is abandoned.
    /// </summary>
    private async Task<(PanelSignIn SignIn, Uri Panel)> AuthenticateAsync(bool fallbackFirst, CancellationToken cancellationToken)
    {
        if (_config.PendingRotation is { } pending)
        {
            INodeKey? newKey = null;
            X509Certificate2? newCertificate = null;
            try
            {
                newKey = keys.Open(pending.Key);
                newCertificate = CreateCertificate(newKey);
                var signedIn = await SignInAsync(pending.CredentialId, newKey, newCertificate, fallbackFirst, cancellationToken);
                CompleteRotation(pending, newKey, newCertificate);
                _pendingKeyMisses = 0;
                return signedIn;
            }
            catch (Exception ex) when (ex is AgentRejectedException or CryptographicException)
            {
                newKey?.Dispose();
                newCertificate?.Dispose();
                (PanelSignIn SignIn, Uri Panel) current;
                try
                {
                    current = await SignInAsync(_config.CredentialId, _key, _certificate, fallbackFirst, cancellationToken);
                }
                catch (AgentRejectedException)
                {
                    LogBothKeysRefused(pending.CredentialId, ex.Message);
                    throw;
                }

                AbandonPendingKey(pending, ex.Message);
                return current;
            }
            catch (PanelNotAnsweringException ex)
            {
                newKey?.Dispose();
                newCertificate?.Dispose();
                // Not a refusal the panel worded, so the key is kept: the current key says whether anything answers there.
                var current = await SignInAsync(_config.CredentialId, _key, _certificate, fallbackFirst, cancellationToken);
                if (++_pendingKeyMisses >= MaxPendingKeyMisses)
                {
                    AbandonPendingKey(pending, $"the panel answered {_pendingKeyMisses} times without recognizing it: {ex.Message}");
                }

                return current;
            }
            catch
            {
                newKey?.Dispose();
                newCertificate?.Dispose();
                throw;
            }
        }

        return await SignInAsync(_config.CredentialId, _key, _certificate, fallbackFirst, cancellationToken);
    }

    /// <summary>
    /// Gives the pending key up once the current key signed in, which the panel granted: it recorded that sign-in, so a
    /// sign-in with the pending key held back on the way is refused from then on.
    /// </summary>
    private void AbandonPendingKey(PendingRotation pending, string reason)
    {
        _pendingKeyMisses = 0;
        // A rotation that failed like any other: the next one waits (a key due by age would otherwise start one
        // again at once, and so on).
        _nextRotationAttempt = clock.GetUtcNow() + options.RotationRetryDelay;
        LogPendingKeyAbandoned(pending.CredentialId, reason, _nextRotationAttempt);
        DeleteKey(pending.Key);
        lock (_configLock)
        {
            SaveConfig(_config with { PendingRotation = null });
        }
    }

    /// <summary>
    /// Signs in at the configured address, at its fallback when nothing answers there (an agents' port closed later) or
    /// when <paramref name="fallbackFirst"/>, and at the agents' address when a panel that requires client certificates
    /// sends it there (Phase 16), keeping that address only once it signed in there.
    /// </summary>
    private async Task<(PanelSignIn SignIn, Uri Panel)> SignInAsync(Guid credentialId, INodeKey key, X509Certificate2? certificate,
        bool fallbackFirst, CancellationToken cancellationToken)
    {
        var config = _config with { CredentialId = credentialId };
        var panel = fallbackFirst && config.FallbackPanel is { } first ? first : config.Panel;
        _attemptPanel = panel;
        try
        {
            return (await authenticator.SignInAsync(panel, config, key, certificate, cancellationToken), panel);
        }
        catch (CertificateRequiredException ex) when (certificate is not null)
        {
            // Without a certificate of its own the agent has nowhere to go: the refusal stands.
            if (ex.RefusedAt is null || AgentsAddressHint.Verify(ex.AgentsAddress, config, ex.RefusedAt, ex.ChallengeNonce) is not { } target)
            {
                LogAgentsAddressNotTrusted();
                throw;
            }

            _attemptPanel = target;
            var signedIn = await authenticator.SignInAsync(target, config, key, certificate, cancellationToken);
            MoveTo(target);
            return (signedIn, target);
        }
        catch (Exception ex) when (panel == config.Panel && config.FallbackPanel is { } fallback && fallback != panel && IsUnreachable(ex, cancellationToken))
        {
            LogPanelUnreachable(panel, fallback, ex.Message);
            return await SignInAsync(credentialId, key, certificate, fallbackFirst: true, cancellationToken);
        }
    }

    /// <summary>The agents' address becomes the configured one; the address used until now stays as the fallback.</summary>
    private void MoveTo(Uri address)
    {
        lock (_configLock)
        {
            SaveConfig(_config.MovedTo(address));
        }

        LogMovedToAgentsAddress(address, _config.FallbackPanel);
    }

    /// <summary>
    /// The panel's agents' address from a command: <paramref name="port"/> on the host this session reached the panel by,
    /// or the operator's <paramref name="url"/>. Kept only if the agent can sign in there now.
    /// </summary>
    public async Task<CommandAck> UseAgentsAddressAsync(Guid commandId, int port, Uri? url)
    {
        INodeKey key;
        X509Certificate2? certificate;
        AgentConfig config;
        lock (_configLock)
        {
            key = _key;
            certificate = _certificate;
            config = _config;
        }

        if (certificate is null || PanelAddress.AgentsAddress(_sessionPanel ?? config.Panel, port, url?.ToString()) is not { } address)
        {
            return new CommandAck(commandId, AckStatus.Rejected, "agent.address_unreachable", "This agent has no client certificate to present there.");
        }

        if (address == config.Panel)
        {
            return new CommandAck(commandId, AckStatus.AlreadyInState, null, null);
        }

        try
        {
            using var timeout = new CancellationTokenSource(options.AgentsAddressTimeout, clock);
            await authenticator.AuthenticateAsync(address, config, key, certificate, timeout.Token);
            MoveTo(address);
        }
        catch (Exception ex) when (ex is AgentException or HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException
            or CryptographicException or System.Text.Json.JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            // Nothing changed: a key rotated away meanwhile, a page that is not the panel's, a configuration not saved.
            LogAgentsAddressUnreachable(address, ex.Message);
            return new CommandAck(commandId, AckStatus.Rejected, "agent.address_unreachable", ex.Message);
        }

        // The session goes on at the new address: the next one opens there while this one still sends the answer.
        _reconnectRequested.TrySetResult();
        return new CommandAck(commandId, AckStatus.Accepted, null, null);
    }

    /// <summary>
    /// Nothing answered at the address as the panel would (connection or TLS refused, no answer, a WebSocket refused, a
    /// 404 or 5xx from whatever is there): not a refusal from the panel, which the fallback would only repeat.
    /// </summary>
    internal static bool IsUnreachable(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException or System.Security.Authentication.AuthenticationException
            or System.Net.WebSockets.WebSocketException or PanelNotAnsweringException or TimeoutException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>The key as a client certificate; without one the agent still works where the panel asks for none.</summary>
    private X509Certificate2? CreateCertificate(INodeKey key)
    {
        var certificate = NodeCertificate.TryCreate(key, out var failure);
        if (certificate is null)
        {
            LogNoCertificate(failure!);
        }

        return certificate;
    }

    private void CompleteRotation(PendingRotation pending, INodeKey newKey, X509Certificate2? newCertificate)
    {
        var old = _key;
        var oldCertificate = _certificate;
        lock (_configLock)
        {
            SaveConfig(_config with { Key = pending.Key, CredentialId = pending.CredentialId, KeyCreatedAt = pending.CreatedAt, PendingRotation = null });
            _key = newKey;
            _certificate = newCertificate;
        }

        DeleteKey(old.Reference);
        old.Dispose();
        // The live session's transfers present the old one until the new session replaces it.
        if (oldCertificate != _sessionCertificate)
        {
            oldCertificate?.Dispose();
        }

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
        try
        {
            return await ConnectAtAsync(fallbackFirst: false, cancellationToken);
        }
        catch (Exception ex) when (_attemptPanel is { } attempted && attempted == _config.Panel && _config.FallbackPanel is { } fallback
            && fallback != attempted && IsUnreachable(ex, cancellationToken))
        {
            // Signed in, but the hub's WebSocket did not open there: the fallback's may.
            LogPanelUnreachable(attempted, fallback, ex.Message);
            return await ConnectAtAsync(fallbackFirst: true, cancellationToken);
        }
    }

    private async Task<Session> ConnectAtAsync(bool fallbackFirst, CancellationToken cancellationToken)
    {
        var (signedIn, panel) = await AuthenticateAsync(fallbackFirst, cancellationToken);
        var token = signedIn.Token;
        var grant = signedIn.Grant;
        var config = _config;
        var certificate = _certificate;
        // Moving to the agents' address takes a certificate: an agent without one does not say it can.
        IReadOnlySet<string> supported = certificate is null
            ? dispatcher.SupportedCommands.Where(c => c != AgentCapabilities.UseAgentsAddress).ToHashSet(StringComparer.Ordinal)
            : dispatcher.SupportedCommands;
        // The session and the panel's clock as the panel's grant says them, never as an unsigned answer does: whatever
        // answers on the way could otherwise pass off a session id and a time of its choosing, and play recorded commands
        // back (docs/architecture/03, "Authentication on every connection").
        var verifier = new CommandVerifier(config.NodeId, grant.SessionId, () => _config.PanelKeys, supported, clock)
        {
            PanelClockOffset = signedIn.PanelClockOffset,
        };
        if (verifier.PanelClockOffset.Duration() > options.ClockSkewWarning)
        {
            LogClockSkew(verifier.PanelClockOffset);
        }

        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(panel, AgentProtocol.HubPath), HttpTransportType.WebSockets, o =>
            {
                o.SkipNegotiation = true;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token.AccessToken);
                if (certificate is not null)
                {
                    o.ClientCertificates!.Add(certificate);
                }

                // The WebSocket goes through the transport's handler: the panel's certificate is checked as for its requests.
                o.WebSocketFactory = (context, ct) => transport.ConnectHubAsync(context.Uri, token.AccessToken, certificate, ct);
                transport.ConfigureHub(o);
            })
            .AddJsonProtocol(o => ProtocolJson.Configure(o.PayloadSerializerOptions));
        builder.Services.AddSingleton(loggerFactory);
        var connection = builder.Build();

        var session = new Session(connection, grant.SessionId, token.AccessToken, system, options, clock, _logger, _channel,
            () => DiskUsageSampler.Sample(config.ServersRootOrDefault(configs.Paths), config.BackupsRootOrDefault(configs.Paths)));
        // A panel or proxy that keeps the connection alive (it answers pings) but never completes the upgrade or answers
        // Hello must not hold the agent here: the session start has a deadline (in real time, as a request's timeout),
        // then counts as a failed connection.
        using var limit = new CancellationTokenSource(options.SessionStartTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            // The client does not await handlers that return a result before reading on, so a command arriving before Hello's
            // answer never holds it back (CommandBeforeHelloTests).
            connection.On<SignedMessage, CommandAck>(nameof(IAgentClient.Execute), message => dispatcher.ExecuteAsync(verifier, message, this));
            await connection.StartAsync(deadline.Token);

            var hello = await connection.InvokeAsync<HelloAccepted>(nameof(IAgentHub.Hello),
                new AgentHello(AgentInfo.Version, AgentProtocol.CurrentVersion, [.. supported],
                    system.GetInventory(config.ServersRootOrDefault(configs.Paths)), [.. config.PanelKeys.Select(k => k.KeyId)],
                    servers?.Snapshots() ?? [], servers?.OperationsInFlight() ?? []),
                deadline.Token);

            // Hello's answer is not signed: its time is not the panel's clock (the grant's is).
            var now = clock.GetUtcNow();
            var lifetimeLeft = grant.ExpiresAt - (now + verifier.PanelClockOffset);
            var lead = options.RenewalLead < lifetimeLeft / 2 ? options.RenewalLead : lifetimeLeft / 2;
            // Never at once: a token already due (a clock far off) would otherwise be renewed in a loop.
            var renewAt = now + lifetimeLeft - lead;
            session.Started(renewAt > now + options.MinimumRenewalDelay ? renewAt : now + options.MinimumRenewalDelay);
            LogConnected(grant.SessionId, verifier.PanelClockOffset);
            // Consoles browsers were already watching: their output resumes where the panel's copy ends.
            servers?.SubscribeConsoles(hello.ConsoleSubscriptions ?? []);
            ForgetDeletedSteamAccounts(hello.SteamAccountIds);
            await _channel.AttachAsync(connection, new PanelHttp(transport, panel, token.AccessToken, certificate), deadline.Token);
            var previousCertificate = _sessionCertificate;
            _sessionPanel = panel;
            _sessionCertificate = certificate;
            if (previousCertificate is not null && previousCertificate != certificate && previousCertificate != _certificate)
            {
                // The certificate of a key rotated away, kept until the session that used it was replaced.
                previousCertificate.Dispose();
            }

            return session;
        }
        catch (OperationCanceledException ex) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await session.DisposeAsync();
            throw new TimeoutException($"The panel did not open the session within {options.SessionStartTimeout.TotalSeconds:0} seconds.", ex);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>Steam accounts deleted in the panel while this node was away: their cached logins go.</summary>
    private void ForgetDeletedSteamAccounts(IReadOnlyList<Guid>? known)
    {
        if (steamLogins is null || known is null)
        {
            return;
        }

        try
        {
            if (steamLogins.ForgetAllBut([.. known]) is > 0 and var forgotten)
            {
                LogSteamLoginsForgotten(forgotten);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSteamLoginsNotForgotten(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Forgot the cached logins of {Count} Steam account(s) the panel no longer has")]
    private partial void LogSteamLoginsForgotten(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The cached logins of Steam accounts the panel no longer has could not be deleted: {Reason}")]
    private partial void LogSteamLoginsNotForgotten(string reason);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel at {Panel} cannot be reached ({Reason}); signing in at the fallback address {Fallback}")]
    private partial void LogPanelUnreachable(Uri panel, Uri fallback, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel requires a client certificate and sent no agents' address this agent can use (unsigned, for another node or request, or the address that refused)")]
    private partial void LogAgentsAddressNotTrusted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved to the panel's agents' address {Panel} (the node key as client certificate); {Fallback} is the fallback")]
    private partial void LogMovedToAgentsAddress(Uri panel, Uri? fallback);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel's agents' address {Panel} did not work ({Reason}); staying at the current address")]
    private partial void LogAgentsAddressUnreachable(Uri panel, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The node key cannot be presented as a client certificate ({Reason}): only addresses that ask for none will work")]
    private partial void LogNoCertificate(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node key rotation failed ({Reason}); the current key stays in use. Next attempt after {NextAttempt}")]
    private partial void LogRotationFailed(string reason, DateTimeOffset nextAttempt);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Abandoning the pending key of credential {CredentialId} ({Reason}); the current key stays in use. Next rotation attempt after {NextAttempt}")]
    private partial void LogPendingKeyAbandoned(Guid credentialId, string reason, DateTimeOffset nextAttempt);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The pending key of credential {CredentialId} did not sign in ({Reason}) and the panel refuses the current key too: the panel may have activated the pending key, so both are kept and tried again")]
    private partial void LogBothKeysRefused(Guid credentialId, string reason);

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
        PanelChannel channel,
        Func<IReadOnlyList<DiskUsage>> disks) : IAsyncDisposable
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

        /// <summary>
        /// Until the connection closes, the session is due for renewal (or asked to move: <paramref name="reconnectRequested"/>),
        /// or a key rotation is requested.
        /// </summary>
        public async Task<SessionEnd> WaitAsync(Task rotationRequested, Task reconnectRequested, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var wait = _renewAt - clock.GetUtcNow();
            var renewal = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, clock, linked.Token);
            var first = await Task.WhenAny(_closed.Task, renewal, rotationRequested, reconnectRequested);
            await linked.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed.Task.IsCompleted)
            {
                return SessionEnd.Closed;
            }

            return first == rotationRequested ? SessionEnd.Rotate : first == reconnectRequested ? SessionEnd.Move : SessionEnd.Renew;
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
                NodeMetrics metrics;
                try
                {
                    metrics = system.SampleMetrics() with
                    {
                        Disks = disks(),
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A Windows call that failed this time: the next sample may work.
                    LogSamplingFailed(ex);
                    continue;
                }

                try
                {
                    await connection.SendAsync(nameof(IAgentHub.NodeMetrics), metrics, cancellationToken);
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Node metrics could not be sampled; trying again at the next interval")]
        private partial void LogSamplingFailed(Exception exception);
    }
}
