using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pulsar.Api.Access;
using Pulsar.Api.Agents;
using Pulsar.Api.Auth;
using Pulsar.Api.Certificates;
using Pulsar.Api.Http;
using Pulsar.Api.Nodes;
using Pulsar.Api.Realtime;
using Pulsar.Api.Security;
using Pulsar.Api.Servers;
using Pulsar.Api.Sftp;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Realtime;
using Pulsar.Application.Servers;
using Pulsar.Contracts;

namespace Pulsar.Api;

/// <summary>Options for <c>RateLimiting:Auth</c> (login and setup attempts per client IP).</summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "RateLimiting:Auth";

    public int PermitLimit { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Requests per minute each API key may make (<c>RateLimiting:Auth:ApiKeyPermitLimit</c>).</summary>
    public int ApiKeyPermitLimit { get; set; } = 600;

    /// <summary>
    /// Lookups per minute, from one address, of API keys not known to work (<c>RateLimiting:Auth:ApiKeyLookupPermitLimit</c>):
    /// guesses, revoked or expired keys, and the first request of a key unused for an hour. Over it such requests are
    /// refused before the database is read; keys in use keep their own budget (<see cref="ApiKeyRequestBudget"/>).
    /// </summary>
    public int ApiKeyLookupPermitLimit { get; set; } = 60;

    /// <summary>Agent enrollments per minute from one address (<c>RateLimiting:Auth:EnrollPermitLimit</c>).</summary>
    public int EnrollPermitLimit { get; set; } = 10;

    /// <summary>
    /// Requests per minute from one address to the anonymous routes that read the database: the session check every page
    /// load makes and readiness (<c>RateLimiting:Auth:AnonymousPermitLimit</c>).
    /// </summary>
    public int AnonymousPermitLimit { get; set; } = 300;

    public const int DefaultLookupPermitLimit = 60;

    /// <summary>
    /// Requests per minute each user may make to the routes that cost more than a database read: Workshop lookups (each
    /// asks Steam) and the previews that check a manifest or a schedule (<c>RateLimiting:Auth:LookupPermitLimit</c>).
    /// </summary>
    public int LookupPermitLimit { get; set; } = DefaultLookupPermitLimit;

    /// <summary>
    /// The least time a failed password check takes, in the browser and over SFTP (<see cref="FailedCheckTime"/>;
    /// <c>RateLimiting:Auth:FailedPasswordCheckTime</c>).
    /// </summary>
    public TimeSpan FailedPasswordCheckTime { get; set; } = FailedCheckTime.Default;
}

internal static class ApiServiceCollectionExtensions
{
    /// <summary>2 MiB: a 256 KiB manifest with every character JSON-escaped still fits.</summary>
    public const long MaxRequestBodyBytes = 2 * 1024 * 1024;

    public static IServiceCollection AddPulsarApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<DomainErrorHandler>();

        // Requests are small JSON documents; the endpoints that take files (uploads, the agent's transfers) raise the
        // limit for themselves. Kestrel's default (30 MB) would let anonymous requests (login, setup, enrollment) send
        // that much before any check.
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

        // The agents' endpoint (Phase 16), when Kestrel:Endpoints:Agents exists: client certificates, agent protocol only.
        services.AddOptions<AgentsOptions>()
            .Bind(configuration.GetSection(AgentsOptions.SectionName))
            .Validate(o => AgentsOptions.IsValidUrl(o.PublicUrl), "Agents:PublicUrl must be an https origin, such as https://agents.example.com:8444.")
            .Validate(o => o.MaxConnections > 0, "Agents:MaxConnections must be at least 1.")
            .Validate(o => o.MaxPendingConnections is null or > 0 && o.PendingLimit <= o.MaxConnections,
                "Agents:MaxPendingConnections must be at least 1 and at most Agents:MaxConnections.")
            .Validate(o => o.MaxPendingConnectionsPerAddress is null or > 0 && o.PendingPerAddressLimit <= o.PendingLimit,
                "Agents:MaxPendingConnectionsPerAddress must be at least 1 and at most Agents:MaxPendingConnections.")
            .Validate(o => o.MaxPendingConnectionsPerNetwork is null
                    || (o.MaxPendingConnectionsPerNetwork >= o.PendingPerAddressLimit && o.MaxPendingConnectionsPerNetwork <= o.PendingLimit),
                "Agents:MaxPendingConnectionsPerNetwork must be at least Agents:MaxPendingConnectionsPerAddress and at most Agents:MaxPendingConnections.")
            .Validate(o => o.PerNodeLimit >= 2 && o.PerNodeLimit <= o.MaxConnections,
                "Agents:MaxConnectionsPerNode must be at least 2 (a node's hub and one transfer) and at most Agents:MaxConnections.")
            .ValidateOnStart();
        services.AddSingleton<AgentsEndpoint>();
        // Transfers take half of the connections a node may hold, so that its hub and rotation always find a place.
        services.AddSingleton(sp => FileTransferLimits.ForNodeConnections(
            sp.GetRequiredService<IOptions<AgentsOptions>>().Value.PerNodeLimit));
        // The browsers' endpoints choose their certificate per connection: the public IP certificate for the internet.
        services.AddOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>()
            .Configure<AgentsEndpoint, PublicIpCertificateSelection>((options, agents, publicIp) => publicIp.ConfigureEndpoints(
                options.Configure(configuration.GetSection("Kestrel"), reloadOnChange: true).Endpoint(AgentsEndpoint.Name, agents.Configure),
                configuration.GetSection("Kestrel:Endpoints")));
        services.AddValidation();
        services.AddHttpContextAccessor();
        services.AddScoped<HttpRequestContext>();
        // Background work (the scheduler) sets who it acts as before resolving anything that audits.
        services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<RequestContextOverride>().Context ?? sp.GetRequiredService<HttpRequestContext>());
        AddSftp(services, configuration);
        AddPublicIpCertificate(services, configuration);
        // Phase 18: the node code's address and certificate come from the administrator's request; the agent's setup
        // from the panel's downloads folder.
        services.AddOptions<DownloadsOptions>().Bind(configuration.GetSection(DownloadsOptions.SectionName));
        services.AddSingleton<AgentSetupDownload>();
        services.AddSingleton<INodeSetup, HttpNodeSetup>();
        services.AddSingleton<InitialSetup>();
        services.AddSingleton<FailedLoginBudget>();
        services.AddSingleton<SignInLocks>();
        services.AddScoped<UserManagement>();

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            // Numbers are numbers: the web default also accepts "42", which leaks into the OpenAPI types as number | string.
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

        // The document drives the generated TypeScript client (src/Pulsar.Web/src/api).
        services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Pulsar API";
            document.Info.Version = "v1";
            document.Info.Description = "Pulsar panel API. Browsers use the session cookie; state-changing requests then need the " +
                $"{AntiforgeryProtection.HeaderName} header with the token from the __Host-pulsar-xsrf cookie " +
                "(pulsar-xsrf when Security:AllowInsecureHttp is on). Scripts use an API key created in the account page: " +
                "`Authorization: Bearer pulsar_…`, no cookie and no antiforgery header. A key works on the operations that " +
                "need a permission (with its user's permissions, or the ones it lists); never on /auth or /account.";
            return Task.CompletedTask;
        }));

        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        var cookies = new PanelCookies(secure: !security.AllowInsecureHttp);
        services.AddSingleton(security);
        services.AddSingleton(cookies);
        // The CSP names Steam's CDN only while the game artwork is on: read before the first page is served.
        services.AddSingleton<SteamArtworkPolicy>();
        services.AddHostedService<SteamArtworkLoader>();
        services.Configure<ForwardedHeadersOptions>(options => TransportSecurity.ConfigureForwardedHeaders(options, security));

        AddAuthentication(services, cookies);
        AddRateLimiting(services, configuration);
        AddAgentChannel(services);

        // Development and CI turn on Pulsar's test game; a production panel never offers it.
        services.AddSingleton(new TemplateOptions { EnableTestGame = configuration.GetValue<bool>("Pulsar:EnableTestGame") });
        services.AddSingleton(configuration.GetSection("Files").Get<FileManagerOptions>() ?? new FileManagerOptions());
        return services;
    }

    /// <summary>The panel's SFTP server (Phase 17): on by default, its settings in the database, its limits here.</summary>
    private static void AddSftp(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SftpOptions>()
            .Bind(configuration.GetSection(SftpOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SftpOptions>, SftpOptionsValidation>();
        services.AddSingleton<SftpHostKey>();
        services.AddSingleton<SftpConnections>();
        services.AddSingleton<SftpAuthenticator>();
        services.AddSingleton<SftpHost>();
        services.AddHostedService(sp => sp.GetRequiredService<SftpHost>());
    }

    /// <summary>The public IP certificate (Let's Encrypt): off by default, its settings in the database, its port here.</summary>
    private static void AddPublicIpCertificate(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PublicIpCertificateOptions>()
            .Bind(configuration.GetSection(PublicIpCertificateOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PublicIpCertificateOptions>, PublicIpCertificateOptionsValidation>();
        services.AddSingleton<PublicIpCertificateStore>();
        services.AddSingleton<PublicIpCertificateSelection>();
        services.AddSingleton<AcmeChallenges>();
        services.AddSingleton<AcmeChallengeListener>();
        services.AddSingleton<PublicIpCertificateService>();
        services.AddHostedService(sp => sp.GetRequiredService<PublicIpCertificateService>());
    }

    private static void AddAgentChannel(IServiceCollection services)
    {
        services.AddSignalR(options => options.MaximumReceiveMessageSize = AgentProtocol.MaximumMessageSize)
            .AddJsonProtocol(options => ProtocolJson.Configure(options.PayloadSerializerOptions))
            // Browsers only send small requests (what they watch); agents send console output in large batches.
            .AddHubOptions<PanelHub>(options => options.MaximumReceiveMessageSize = 32 * 1024)
            .AddHubOptions<AgentHub>(options => options.AddFilter<AgentHubLimits>());
        services.AddSingleton<AgentHubLimits>();
        services.AddSingleton<ConsoleMirrors>();
        services.AddSingleton<BrowserConnections>();
        services.AddSingleton<LiveMetrics>();
        services.Replace(ServiceDescriptor.Singleton<IRealtimeNotifier, PanelNotifier>());
        services.AddSingleton<AgentConnectionRegistry>();
        services.AddSingleton<IAgentConnections>(sp => sp.GetRequiredService<AgentConnectionRegistry>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuthenticationHandler.Scheme, null);
    }

    private static void AddAuthentication(IServiceCollection services, PanelCookies cookies)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddPolicyScheme(UserSchemes.CookieOrApiKey, "Session cookie or API key", options => options.ForwardDefaultSelector = UserSchemes.Select)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.Scheme, null)
            .AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = cookies.Session;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = cookies.SecurePolicy;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            // Sliding, but never past SessionLifetime.MaxAge from the sign-in; then the security stamp, as Identity does.
            options.Events.OnSigningIn = SessionLifetime.OnSigningIn;
            options.Events.OnValidatePrincipal = SessionLifetime.OnValidatePrincipal;

            // An API answers 401/403; the SPA decides where to go.
            options.Events.OnRedirectToLogin = context => WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized,
                "auth.required", "Sign in to continue.");
            options.Events.OnRedirectToAccessDenied = context => WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden,
                "auth.forbidden", "You do not have permission to do this.");
        });

        // Between the password and the second factor (five minutes, Identity's default).
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.Name = cookies.TwoFactor;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = cookies.SecurePolicy;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        // Disabled users and changed passwords end existing sessions within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthorization();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, TwoFactorAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PulsarAuthorizationResultHandler>();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryProtection.HeaderName;
            options.Cookie.Name = cookies.AntiforgeryCookieToken;
            options.Cookie.SecurePolicy = cookies.SecurePolicy;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
    }

    private static void AddRateLimiting(IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(AuthRateLimitOptions.SectionName).Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
        services.AddOptions<AuthRateLimitOptions>().Bind(configuration.GetSection(AuthRateLimitOptions.SectionName));
        var downloads = configuration.GetSection(DownloadsOptions.SectionName).Get<DownloadsOptions>() ?? new DownloadsOptions();
        if (downloads.ConcurrentPerAddress < 1 || downloads.PerAddressPerHour < 1)
        {
            throw new InvalidOperationException($"{DownloadsOptions.SectionName}:ConcurrentPerAddress and PerAddressPerHour must be at least 1.");
        }

        if (limits.ApiKeyPermitLimit < 1 || limits.ApiKeyLookupPermitLimit < 1)
        {
            throw new InvalidOperationException($"{AuthRateLimitOptions.SectionName}:ApiKeyPermitLimit and ApiKeyLookupPermitLimit must be at least 1.");
        }

        // Each API key has its own budget (a script in a loop must not slow the panel down), checked before the key is
        // looked up (ApiKeyRateLimit); browsers have none.
        services.AddSingleton<ApiKeyRequestBudget>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(AuthEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress.PartitionOf(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.PermitLimit, Window = limits.Window, QueueLimit = 0 }));
            // Anyone may ask, and each answer takes a database connection: a flood must not take the pool from sign-ins.
            options.AddPolicy(AuthEndpoints.AnonymousRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress.PartitionOf(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AnonymousPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Per user (all of a household's browsers share one address), or per address for a request without one.
            options.AddPolicy(WorkshopEndpoints.LookupRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.GetUserId()?.ToString() ?? ClientAddress.PartitionOf(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.LookupPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(AgentEndpoints.EnrollRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress.PartitionOf(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.EnrollPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(AgentEndpoints.AuthRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress.PartitionOf(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Per node, whoever asks: each answer is the agent listing its machine's ports.
            options.AddPolicy(NodeEndpoints.PortsInUseRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.GetRouteValue("id")?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = NodeEndpoints.PortsInUsePermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // The agent's setup, anonymous and a whole program each time: so many at once and an hour per address.
            options.AddPolicy(DownloadEndpoints.RateLimitPolicy, context => RateLimitPartition.Get(
                ClientAddress.PartitionOf(context.Connection.RemoteIpAddress), _ => DownloadEndpoints.CreateLimiter(downloads)));
            options.OnRejected = (context, _) => new ValueTask(WriteRateLimitedAsync(context.HttpContext, context.Lease));
        });
    }

    /// <summary>The answer to a request over a rate limit.</summary>
    public static Task WriteRateLimitedAsync(HttpContext context, RateLimitLease lease)
    {
        // When the window ends: agents enrolling together (a rollout behind one address) wait that long.
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        return WriteProblemAsync(context, StatusCodes.Status429TooManyRequests, "rate_limit.exceeded", "Too many attempts. Try again in a minute.");
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        return ApiProblems.Create(status, code, title).ExecuteAsync(context);
    }
}
