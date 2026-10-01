using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Pulsar.Api.Access;
using Pulsar.Api.Agents;
using Pulsar.Api.Auth;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api;

/// <summary>Options for <c>RateLimiting:Auth</c> (login and setup attempts per client IP).</summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "RateLimiting:Auth";

    public int PermitLimit { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

internal static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddPulsarApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProblemDetails();
        services.AddValidation();
        services.AddHttpContextAccessor();
        services.AddScoped<IRequestContext, HttpRequestContext>();
        services.AddSingleton<InitialSetup>();
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
            document.Info.Description = "Pulsar panel API. Cookie authentication; state-changing requests need the " +
                $"{AntiforgeryProtection.HeaderName} header with the token from the __Host-pulsar-xsrf cookie " +
                "(pulsar-xsrf when Security:AllowInsecureHttp is on).";
            return Task.CompletedTask;
        }));

        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        var cookies = new PanelCookies(secure: !security.AllowInsecureHttp);
        services.AddSingleton(security);
        services.AddSingleton(cookies);
        services.Configure<ForwardedHeadersOptions>(options => TransportSecurity.ConfigureForwardedHeaders(options, security));

        AddAuthentication(services, cookies);
        AddRateLimiting(services, configuration);
        AddAgentChannel(services);
        return services;
    }

    private static void AddAgentChannel(IServiceCollection services)
    {
        services.AddSignalR(options => options.MaximumReceiveMessageSize = AgentProtocol.MaximumMessageSize)
            .AddJsonProtocol(options => ProtocolJson.Configure(options.PayloadSerializerOptions));
        services.AddSingleton<AgentConnectionRegistry>();
        services.AddSingleton<IAgentConnections>(sp => sp.GetRequiredService<AgentConnectionRegistry>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuthenticationHandler.Scheme, null);
    }

    private static void AddAuthentication(IServiceCollection services, PanelCookies cookies)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = cookies.Session;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = cookies.SecurePolicy;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;

            // An API answers 401/403; the SPA decides where to go.
            options.Events.OnRedirectToLogin = context => WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized,
                "auth.required", "Sign in to continue.");
            options.Events.OnRedirectToAccessDenied = context => WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden,
                "auth.forbidden", "You do not have permission to do this.");
        });

        // Disabled users and changed passwords end existing sessions within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthorization();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

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
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(AuthEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.PermitLimit, Window = limits.Window, QueueLimit = 0 }));
            options.AddPolicy(AgentEndpoints.EnrollRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(AgentEndpoints.AuthRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = (context, _) => new ValueTask(WriteProblemAsync(context.HttpContext,
                StatusCodes.Status429TooManyRequests, "rate_limit.exceeded", "Too many attempts. Try again in a minute."));
        });
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        return ApiProblems.Create(status, code, title).ExecuteAsync(context);
    }
}
