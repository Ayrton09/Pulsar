using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <summary>What the panel can do to the players of a server's game (its template's <c>commands.players</c>).</summary>
/// <param name="ById">The commands take an id, not the names the game's query lists.</param>
/// <param name="Spaces">A player's name may have spaces.</param>
/// <param name="Lists">The lists of players the game keeps in files the panel reads.</param>
public sealed record ServerPlayersView(bool ById, bool Spaces, IReadOnlyList<PlayerAction> Actions, IReadOnlyList<PlayerListKind> Lists);

/// <summary>A list of players as the game's file has it.</summary>
/// <param name="Players">The names (or ids), in the file's order, each once.</param>
/// <param name="Truncated">The file lists more than <see cref="ServerPlayers.MaxListed"/>.</param>
/// <param name="ErrorCode">The file could not be read (<c>players.list_unreadable</c>): its list is empty.</param>
public sealed record PlayerListView(PlayerListKind Kind, IReadOnlyList<string> Players, bool Truncated, string? ErrorCode = null);

public sealed record ServerPlayerLists(IReadOnlyList<PlayerListView> Lists);

/// <summary>
/// Kick, ban, whitelist and make admins of a server's players (docs/architecture/07, "Players"): the game's own commands
/// (<see cref="PlayerCommandsDefinition"/>), sent through its console and audited as player actions. A name is sent only
/// as <see cref="GameCommands.PlayerName"/> takes it, never cleaned. Making a player the game's admin, or taking it away,
/// needs the right to send console commands too: an admin runs the game's commands in game.
/// </summary>
public sealed class ServerPlayers(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    ServerFiles files,
    IPermissionEvaluator permissions,
    IAuditLog audit,
    IRequestContext request)
{
    /// <summary>The most names a list shows.</summary>
    public const int MaxListed = 1000;

    /// <summary>The largest list file the panel reads.</summary>
    public const long MaxListBytes = 256 * 1024;

    public const string ListUnreadable = "players.list_unreadable";

    // A name the game wrote that is longer is not a player (the panel shows no more of it).
    private const int MaxListedLength = 128;

    public static ServerPlayersView? View(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Commands?.Players is { } players
            ? new ServerPlayersView(players.ById, players.Spaces, GameCommands.Actions(manifest),
                [.. Enum.GetValues<PlayerListKind>().Where(k => GameCommands.List(manifest, k) is not null)])
            : null;
    }

    public async Task<Result<AgentCommandResult>> ActAsync(ServerId id, PlayerAction action, string? player, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var manifest = await ManifestAsync(server, cancellationToken);
        if (manifest.Commands?.Players is not { } players || GameCommands.Template(players, action) is null)
        {
            return ServerErrors.PlayerActionUnsupported;
        }

        if (GameCommands.PlayerName(player, players.Spaces) is not { } name)
        {
            return ServerErrors.PlayerInvalid;
        }

        if (action is PlayerAction.AdminAdd or PlayerAction.AdminRemove
            && (request.UserId is not { } user
                || !await permissions.HasAsync(user, Permissions.Server.ConsoleWrite, AccessTarget.ForServer(server.Id, server.NodeId), cancellationToken)))
        {
            return ServerErrors.PlayerAdminForbidden;
        }

        if (server.RuntimeState is not (ServerRuntimeState.Starting or ServerRuntimeState.Running))
        {
            return ServerErrors.NotRunning;
        }

        if (connections.Find(server.NodeId) is not { } live || !sessions.IsActive(live.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        // Once sent, the node's answer is waited for (it has its own timeout) even if the request goes away: what the
        // node did must still be audited.
        var line = GameCommands.Player(manifest, action, name)!;
        var ack = await connections.SendAsync(server.NodeId, new SendConsoleInputCommand(id.Value, line), request.UserId, CancellationToken.None);
        if (ack is null)
        {
            return NodeErrors.NotConnected;
        }

        var accepted = ack.Status == AckStatus.Accepted;
        audit.Record(AuditActions.Server.PlayerAction, accepted ? AuditOutcome.Success : AuditOutcome.Failure,
                new { action = JsonNamingPolicy.CamelCase.ConvertName(action.ToString()), player = name, errorCode = accepted ? null : ack.ErrorCode })
            .WithServer(server.Id, server.NodeId);
        await db.SaveChangesAsync(CancellationToken.None);
        return new AgentCommandResult(ack.Status, ack.ErrorCode);
    }

    /// <summary>
    /// The lists of players the game keeps (who is banned, whitelisted, admin), read from its files on the node. A file the
    /// game has not written yet is an empty list; one the panel cannot read says so, and the others are still read.
    /// </summary>
    public async Task<Result<ServerPlayerLists>> ListsAsync(ServerId id, CancellationToken cancellationToken)
    {
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        var manifest = await ManifestAsync(server, cancellationToken);
        var lists = new List<PlayerListView>();
        foreach (var kind in Enum.GetValues<PlayerListKind>())
        {
            if (GameCommands.List(manifest, kind) is not { } file)
            {
                continue;
            }

            var opened = await files.OpenAsync(id, file.File, MaxListBytes, cancellationToken);
            if (!opened.IsSuccess)
            {
                var error = opened.Error!;
                if (error.Code == FileErrors.NotFound)
                {
                    lists.Add(new PlayerListView(kind, [], false));
                    continue;
                }

                // The node, the server or the transfers, not this file: no list can be read.
                if (!error.Code.StartsWith("files.", StringComparison.Ordinal) || error == ServerErrors.FileTransfersBusy || error == ServerErrors.FilesUnsupported)
                {
                    return error;
                }

                lists.Add(new PlayerListView(kind, [], false, ListUnreadable));
                continue;
            }

            using var content = opened.Value;
            var bytes = await ReadAsync(content, cancellationToken);
            lists.Add(bytes is not null && Names(bytes, file.Field) is { } names
                ? new PlayerListView(kind, [.. names.Take(MaxListed)], names.Count > MaxListed)
                : new PlayerListView(kind, [], false, ListUnreadable));
        }

        return new ServerPlayerLists(lists);
    }

    /// <summary>
    /// The <paramref name="field"/> of each object of the JSON array in <paramref name="json"/>, each once; null when it is
    /// not such an array. The game wrote it (and its players chose their names): what is not a short line of text is left out.
    /// </summary>
    internal static List<string>? Names(byte[] json, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 and <= MaxListedLength } name && !name.Any(char.IsControl) && seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadAsync(FileContent content, CancellationToken cancellationToken)
    {
        if (content.Length > MaxListBytes)
        {
            return null;
        }

        var bytes = new byte[content.Length];
        try
        {
            // A transfer that was ended (its user's sessions, its time) breaks off like a node that went away.
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, content.Aborted);
            await content.Content.ReadExactlyAsync(bytes, reading.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        content.Complete();
        return bytes;
    }

    private async Task<GameManifest> ManifestAsync(Server server, CancellationToken cancellationToken) =>
        ServerSpecs.Parse(await db.GameTemplates.AsNoTracking().SingleAsync(t => t.Id == server.TemplateId, cancellationToken));
}
