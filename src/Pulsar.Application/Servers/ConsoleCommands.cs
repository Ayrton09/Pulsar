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
        // Once sent, the node's answer is waited for (it has its own timeout) even if the request goes away: what the
        // node did must still be audited.
        var ack = await connections.SendAsync(server.NodeId, new SendConsoleInputCommand(id.Value, line), request.UserId, CancellationToken.None);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        var accepted = ack.Status == AckStatus.Accepted;
        audit.Record(AuditActions.Server.ConsoleCommand, accepted ? AuditOutcome.Success : AuditOutcome.Failure,
                new { command = Redact(line, prefixes), errorCode = accepted ? null : ack.ErrorCode })
            .WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        return new AgentCommandResult(ack.Status, ack.ErrorCode);
    }

    /// <summary>
    /// The command as audited: each ';'-separated command (Source consoles run them one by one) that starts with a
    /// sensitive prefix (case-insensitive, after any leading spaces or quotes) becomes <c>rcon_password ***</c>; the
    /// line is unchanged when none does. A ';' inside quotes is split too: redacting more than needed is harmless.
    /// </summary>
    public static string Redact(string line, IReadOnlyList<string> sensitivePrefixes)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(sensitivePrefixes);
        var commands = line.Split(';');
        var redacted = false;
        for (var i = 0; i < commands.Length; i++)
        {
            var start = commands[i].TrimStart(' ', '\t', '"');
            if (sensitivePrefixes.FirstOrDefault(p => start.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
            {
                // Keeps the space after a ';' ("say hi; rcon_password ***"), not the quotes or the line's leading spaces.
                var leading = i == 0 ? string.Empty : commands[i][..^start.Length].Replace("\"", string.Empty, StringComparison.Ordinal);
                commands[i] = $"{leading}{prefix} ***";
                redacted = true;
            }
        }

        return redacted ? string.Join(';', commands) : line;
    }
}
