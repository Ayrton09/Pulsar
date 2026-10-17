using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Pulsar.Api.Http;

namespace Pulsar.Api.Certificates;

/// <summary>
/// The http-01 responder (RFC 8555 section 8.3) on port 80, only while the public IP certificate is on: a Kestrel server
/// of its own, apart from the panel's (none of the panel's middleware, routes, files or authentication), that answers
/// exactly <c>GET /.well-known/acme-challenge/&lt;token&gt;</c> (no body) for a token being validated now with its key
/// authorization as text/plain, and 404 with an empty body to everything else: other paths, other methods, a query, a
/// token not pending, a request target in absolute form or percent-encoded. No redirect, no other content, no listing.
/// HTTP/1.1 only, no request body, a request line of 1 KiB and headers of 4 KiB at most, 5 seconds for the headers and
/// between requests, and a bounded number of connections, from one address too (docs/architecture/08, T40).
/// </summary>
internal sealed partial class AcmeChallengeListener(
    IOptions<PublicIpCertificateOptions> options, AcmeChallenges challenges, ILoggerFactory loggerFactory, ILogger<AcmeChallengeListener> logger)
    : IAsyncDisposable
{
    public const string PathPrefix = "/.well-known/acme-challenge/";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private WebApplication? _app;
    private volatile int _port;
    private volatile string? _error;

    /// <summary>The port it listens on, or null while it does not.</summary>
    public int? ListeningPort => _port is > 0 and var port ? port : null;

    /// <summary>Why it could not listen the last time it tried (the port taken, say), or null.</summary>
    public string? Error => _error;

    /// <summary>Listens unless it already does; a failure is kept in <see cref="Error"/> (tried again at the next call).</summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null)
            {
                return true;
            }

            var settings = options.Value;
            var port = settings.ChallengePortOverride ?? settings.ChallengePort;
            var app = Build(settings, port);
            try
            {
                await app.StartAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                await app.DisposeAsync();
                _error = $"Port {port} could not be opened: {ex.GetBaseException().Message}";
                LogListenFailed(logger, port, ex.GetBaseException().Message);
                return false;
            }

            _app = app;
            _port = BoundPort(app) ?? port;
            _error = null;
            LogListening(logger, _port);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Stops listening (the feature was turned off, or the panel stops).</summary>
    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _error = null;
            if (_app is not { } app)
            {
                return;
            }

            _app = null;
            _port = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stop.Token);
            await app.DisposeAsync();
            LogStopped(logger);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lock.Dispose();
    }

    /// <summary>What a request gets: the key authorization of a pending token, or nothing (404).</summary>
    internal async Task HandleAsync(HttpContext context)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.ContentLength is null or 0 && context.Request.Headers.TransferEncoding.Count == 0
            && target is not null && target.StartsWith(PathPrefix, StringComparison.Ordinal)
            && AcmeClient.IsToken(target.AsSpan(PathPrefix.Length)) && challenges.TryGet(target[PathPrefix.Length..], out var keyAuthorization))
        {
            var body = Encoding.ASCII.GetBytes(keyAuthorization);
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/plain";
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, context.RequestAborted);
            var client = ClientAddress.PartitionOf(context.Connection.RemoteIpAddress);
            LogServed(logger, client);
            return;
        }

        response.StatusCode = StatusCodes.Status404NotFound;
        response.ContentLength = 0;
    }

    private WebApplication Build(PublicIpCertificateOptions settings, int port)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddSingleton(loggerFactory);
        builder.Services.AddSingleton<IHostLifetime, QuietLifetime>();
        builder.WebHost.UseKestrelCore();
        var budget = new ConnectionBudget(settings.MaxChallengeConnectionsPerAddress);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = settings.MaxChallengeConnections;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 0;
            kestrel.Limits.MaxRequestBodySize = 0;
            kestrel.Limits.MaxRequestLineSize = 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 4096;
            kestrel.Limits.MaxRequestHeaderCount = 32;
            kestrel.Limits.MaxResponseBufferSize = 4096;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            void Listen(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.Use(next => async connection =>
                {
                    var key = ClientAddress.PartitionOf((connection.RemoteEndPoint as IPEndPoint)?.Address);
                    if (!budget.TryTake(key))
                    {
                        connection.Abort();
                        return;
                    }

                    try
                    {
                        await next(connection);
                    }
                    finally
                    {
                        budget.Return(key);
                    }
                });
            }

            if (settings.ChallengeBindAddress is null or "*")
            {
                kestrel.ListenAnyIP(port, Listen);
            }
            else
            {
                kestrel.Listen(IPAddress.Parse(settings.ChallengeBindAddress), port, Listen);
            }
        });
        var app = builder.Build();
        app.Run(HandleAsync);
        return app;
    }

    private static int? BoundPort(WebApplication app)
    {
        foreach (var address in app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return null;
    }

    /// <summary>Connections from one client address at once.</summary>
    private sealed class ConnectionBudget(int perAddress)
    {
        private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);

        public bool TryTake(string key)
        {
            lock (_held)
            {
                var held = _held.GetValueOrDefault(key);
                if (held >= perAddress)
                {
                    return false;
                }

                _held[key] = held + 1;
                return true;
            }
        }

        public void Return(string key)
        {
            lock (_held)
            {
                if (_held.TryGetValue(key, out var held))
                {
                    if (held <= 1)
                    {
                        _held.Remove(key);
                    }
                    else
                    {
                        _held[key] = held - 1;
                    }
                }
            }
        }
    }

    /// <summary>No console hooks and no startup messages: the panel's own host owns the process.</summary>
    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Answering Let's Encrypt's http-01 challenges on port {Port}")]
    private static partial void LogListening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped answering http-01 challenges")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Port} for Let's Encrypt's http-01 challenges could not be opened: {Reason}")]
    private static partial void LogListenFailed(ILogger logger, int port, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Served an http-01 challenge to {Client}")]
    private static partial void LogServed(ILogger logger, string client);
}
