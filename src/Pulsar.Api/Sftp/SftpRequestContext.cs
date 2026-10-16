using System.Net;
using System.Text;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Sftp;

/// <summary>
/// <see cref="IRequestContext"/> for what an SFTP connection does: its signed-in user (none before the sign-in), the
/// client's address, and "SFTP" with the client's version where the web has the browser's user agent, so the audit log
/// tells SFTP apart. One correlation id per connection. Never an API key: limits per key do not apply.
/// </summary>
internal sealed class SftpRequestContext : IRequestContext
{
    public const string Marker = "SFTP";

    private SftpRequestContext(UserId? userId, string? userName, AuditRequestInfo requestInfo)
    {
        UserId = userId;
        Actor = userId is { } id ? AuditActor.ForUser(id, userName is null ? Marker : $"{userName} · {Marker}") : AuditActor.Anonymous;
        RequestInfo = requestInfo;
    }

    public UserId? UserId { get; }

    public AuditActor Actor { get; }

    public AuditRequestInfo RequestInfo { get; }

    /// <summary>Before the sign-in: anonymous, from <paramref name="address"/>.</summary>
    public static SftpRequestContext Anonymous(IPAddress? address, string? clientVersion, string correlationId) =>
        new(null, null, new AuditRequestInfo(address, Describe(clientVersion), correlationId));

    /// <summary>The same connection, signed in as <paramref name="userId"/>.</summary>
    public SftpRequestContext SignedIn(UserId userId, string? userName) => new(userId, userName, RequestInfo);

    // "SFTP SSH-2.0-OpenSSH_9.5": printable ASCII only, as short as a user agent is kept.
    private static string Describe(string? clientVersion)
    {
        var text = new StringBuilder(Marker);
        if (!string.IsNullOrEmpty(clientVersion))
        {
            text.Append(' ');
            foreach (var c in clientVersion)
            {
                if (text.Length >= 200)
                {
                    break;
                }

                text.Append(c is >= ' ' and <= '~' ? c : '?');
            }
        }

        return text.ToString();
    }
}
