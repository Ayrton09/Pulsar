using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Abstractions;

/// <summary>Who is making the current request and from where. Implemented by the API host.</summary>
public interface IRequestContext
{
    /// <summary>The signed-in user, or null for anonymous requests and background work.</summary>
    UserId? UserId { get; }

    AuditActor Actor { get; }

    AuditRequestInfo RequestInfo { get; }

    /// <summary>
    /// The only permissions the request may use (an API key limited to some), on top of its user's; null: no limit.
    /// </summary>
    IReadOnlySet<string>? PermissionLimit => null;
}
