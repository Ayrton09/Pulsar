using System.Diagnostics;
using System.Security.Claims;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id (Identity stores it in the NameIdentifier claim), or null.</summary>
    public static UserId? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? new UserId(id) : null;

    public static UserId GetRequiredUserId(this ClaimsPrincipal principal) =>
        principal.GetUserId() ?? throw new InvalidOperationException("The endpoint requires a signed-in user.");
}

/// <summary><see cref="IRequestContext"/> for HTTP requests.</summary>
internal sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public UserId? UserId => Context?.User.GetUserId();

    public AuditActor Actor => UserId is not { } id
        ? AuditActor.Anonymous
        : Context!.User.IsApiKey()
            ? AuditActor.ForApiKey(id, Context.User.Identity?.Name, Context.User.FindFirstValue(ApiKeyClaims.KeyName) ?? "?")
            : AuditActor.ForUser(id, Context.User.Identity?.Name);

    public IReadOnlySet<string>? PermissionLimit =>
        Context?.User is { } user && user.IsApiKey() && user.FindAll(ApiKeyClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal) is
        { Count: > 0 } limit
            ? limit
            : null;

    public AuditRequestInfo RequestInfo => Context is { } context
        ? new AuditRequestInfo(
            context.Connection.RemoteIpAddress,
            context.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null,
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier)
        : AuditRequestInfo.None;

    private HttpContext? Context => accessor.HttpContext;
}
