using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <summary>
/// Commands typed into a server's console (docs/architecture/07, "Console"). Every one is audited; commands starting
/// with one of the game's sensitive prefixes (<c>rcon_password</c>, say) are recorded without their arguments.
/// </summary>
public sealed class ConsoleCommands(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    IAuditLog audit,
    IRequestContext request)
{
    public async Task<Result<AgentCommandResult>> SendAsync(ServerId id, string line, CancellationToken cancellationToken)
    {
        if (!ConsoleInput.IsValid(line))
        {
            return ServerErrors.ConsoleInputInvalid;
        }

        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (server.RuntimeState is not (ServerRuntimeState.Starting or ServerRuntimeState.Running))
        {
            return ServerErrors.NotRunning;
        }

        if (connections.Find(server.NodeId) is not { } live || !sessions.IsActive(live.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        var template = await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken);
        var prefixes = ServerSpecs.Parse(template).Runtime.GetValueOrDefault(ManifestValidator.WindowsX64)?.Console.SensitiveCommandPrefixes ?? [];
        var ack = await connections.SendAsync(server.NodeId, new SendConsoleInputCommand(id.Value, line), request.UserId, cancellationToken);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        var accepted = ack.Status == AckStatus.Accepted;
        audit.Record(AuditActions.Server.ConsoleCommand, accepted ? AuditOutcome.Success : AuditOutcome.Failure,
                new { command = Redact(line, prefixes), errorCode = accepted ? null : ack.ErrorCode })
            .WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(cancellationToken);
        return new AgentCommandResult(ack.Status, ack.ErrorCode);
    }

    /// <summary>
    /// The command as audited: <c>rcon_password ***</c> when it starts with a sensitive prefix (case-insensitive, after
    /// any leading spaces or quotes, as Source consoles accept them), unchanged otherwise.
    /// </summary>
    public static string Redact(string line, IReadOnlyList<string> sensitivePrefixes)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(sensitivePrefixes);
        var start = line.TrimStart(' ', '\t', '"');
        return sensitivePrefixes.FirstOrDefault(p => start.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix
            ? $"{prefix} ***"
            : line;
    }
}
