using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Metrics;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Api.Servers;

/// <param name="Variables">Values of the template's variables (missing ones take their default).</param>
/// <param name="Ports">Allocation per template port (missing required ports get a free one of the node).</param>
/// <param name="PortNumbers">
/// Port number per template port, instead of an allocation: the node's allocation on that number, added to the node
/// when it has none and the user may manage the node. Ports that follow one come with it.
/// </param>
public sealed record CreateServerRequest(
    Guid NodeId,
    Guid TemplateId,
    [property: Required, StringLength(Server.MaxNameLength)] string Name,
    [property: StringLength(Server.MaxDescriptionLength)] string? Description,
    Dictionary<string, string?>? Variables = null,
    Dictionary<string, Guid?>? Ports = null,
    Dictionary<string, int?>? PortNumbers = null);

/// <param name="Variables">Only the variables to change; null or empty puts the default back (or clears a secret).</param>
public sealed record UpdateServerSettingsRequest(
    Dictionary<string, string?> Variables,
    [property: Required] List<string> ExtraArguments,
    bool AutoStart,
    bool RestartOnCrash);

/// <param name="Ports">Allocation per template port; null leaves an optional port without one, a port left out keeps its own.</param>
/// <param name="PortNumbers">Port number per template port, instead of an allocation (as when creating a server).</param>
public sealed record UpdateServerPortsRequest(Dictionary<string, Guid?>? Ports = null, Dictionary<string, int?>? PortNumbers = null);

/// <param name="SteamGuardCode">The code Steam asked for when the last update signed in with the server's account.</param>
public sealed record UpdateGameRequest(bool Validate, [property: StringLength(16)] string? SteamGuardCode = null);

/// <param name="SteamGuardCode">The code Steam asked for when the last install signed in with the server's account.</param>
public sealed record InstallRequest([property: Required, StringLength(16)] string SteamGuardCode);

/// <param name="SteamAccountId">The Steam account SteamCMD signs in with; null: anonymous.</param>
public sealed record ServerSteamAccountRequest(Guid? SteamAccountId);

/// <param name="Password">Required to create; leave it out on an update to keep the current one.</param>
public sealed record SteamAccountRequest(
    [property: Required, StringLength(SteamAccount.MaxNameLength)] string Name,
    [property: Required, StringLength(SteamAccount.MaxUserNameLength)] string UserName,
    [property: StringLength(64)] string? Password)
{
    internal SteamAccountDefinition ToDefinition() => new(Name, UserName, Password);
}

/// <param name="TemplateId">A newer version of the server's game template.</param>
public sealed record UpgradeTemplateRequest(Guid TemplateId);

/// <param name="NodeId">The node the server moves to.</param>
public sealed record TransferServerRequest(Guid NodeId);

/// <param name="NodeId">The node the clone goes to: the server's own, or another.</param>
/// <param name="Name">The clone's name.</param>
public sealed record CloneServerRequest(Guid NodeId, [property: Required, StringLength(Server.MaxNameLength)] string Name);

/// <param name="TemplateId">The game template the server becomes (its own too: it starts again from nothing).</param>
/// <param name="Variables">The new game's first values, as at a server's creation.</param>
public sealed record ChangeGameRequest(Guid TemplateId, Dictionary<string, string?>? Variables = null);

/// <param name="CpuPercent">Share of the node's CPU, 1–100; null: no cap. Both caps are replaced by every request.</param>
/// <param name="MemoryMb">Memory the server's processes may commit, in MiB; null: no cap.</param>
/// <param name="Databases">How many databases the server may have, 0–50; null or left out keeps the current limit.</param>
public sealed record ServerLimitsRequest(
    [property: Range(1, 100)] int? CpuPercent,
    [property: Range(Server.MinMemoryLimitMb, Server.MaxMemoryLimitMb)] int? MemoryMb,
    [property: Range(0, Server.MaxDatabaseLimit)] int? Databases = null);

public sealed record ConsoleCommandRequest([property: Required, StringLength(Pulsar.Contracts.ConsoleInput.MaxLength, MinimumLength = 1)] string Command);

/// <param name="Player">The player's name as the game has it (or its id, for a game whose commands take one).</param>
public sealed record PlayerActionRequest(PlayerAction Action, [property: Required, StringLength(GameCommands.MaxPlayerLength, MinimumLength = 1)] string Player);

public sealed record TemplateContentRequest([property: Required, StringLength(Pulsar.Manifests.ManifestParser.MaximumSize)] string Content);

public static class ServerEndpoints
{
    public static RouteGroupBuilder MapServerEndpoints(this RouteGroupBuilder api)
    {
        var servers = api.MapGroup("/servers").WithTags("Servers");
        servers.MapGet("/", ListAsync).RequirePermissionAnywhere(Permissions.Server.View);
        servers.MapGet("/{id:guid}", GetAsync).RequirePermission(Permissions.Server.View);
        servers.MapPost("/", CreateAsync).RequirePermissionAnywhere(Permissions.Server.Create); // checked on the node by ServerManagement
        servers.MapPost("/{id:guid}/start", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.StartAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Start);
        servers.MapPost("/{id:guid}/stop", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.StopAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Stop);
        servers.MapPost("/{id:guid}/restart", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.RestartAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Restart);
        servers.MapPost("/{id:guid}/kill", (Guid id, ServerManagement m, CancellationToken ct) => CommandAsync(m.KillAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Kill);
        servers.MapPost("/{id:guid}/install", (Guid id, ServerManagement m, CancellationToken ct) => DetailsAsync(m.ReinstallAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Install);
        // The same, answering the Steam Guard code Steam asked for when the last install signed in.
        servers.MapPost("/{id:guid}/install/steam-guard", (Guid id, InstallRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.ReinstallAsync(new ServerId(id), ct, request.SteamGuardCode)))
            .RequirePermission(Permissions.Server.Install);
        servers.MapDelete("/{id:guid}", (Guid id, ServerManagement m, CancellationToken ct) => DetailsAsync(m.DeleteAsync(new ServerId(id), ct)))
            .RequirePermission(Permissions.Server.Delete);
        servers.MapGet("/{id:guid}/metrics", async Task<Results<Ok<ServerMetricHistory>, ProblemHttpResult>> (Guid id, IPulsarDbContext db,
                MetricHistory history, CancellationToken ct, string range = "hour") =>
                MetricHistory.ParseRange(range) is not { } span ? ApiProblems.Create(StatusCodes.Status400BadRequest, "metrics.invalid_range", "The range must be hour, day, week or month.")
                : await db.Servers.AnyAsync(s => s.Id == new ServerId(id), ct) ? TypedResults.Ok(await history.ReadServerAsync(new ServerId(id), span, ct))
                : ApiProblems.FromDomainError(ServerErrors.NotFound))
            .RequirePermission(Permissions.Server.View);
        servers.MapGet("/{id:guid}/settings", async (Guid id, ServerManagement m, CancellationToken ct) => (await m.GetSettingsAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.View);
        servers.MapPut("/{id:guid}/settings", async (Guid id, UpdateServerSettingsRequest request, ServerManagement m, CancellationToken ct) =>
                (await m.UpdateSettingsAsync(new ServerId(id), request.Variables, request.ExtraArguments, request.AutoStart, request.RestartOnCrash, ct)).ToHttp())
            .RequirePermission(Permissions.Server.StartupEdit);
        servers.MapPut("/{id:guid}/ports", (Guid id, UpdateServerPortsRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.UpdatePortsAsync(new ServerId(id), request.Ports ?? [], ct, request.PortNumbers)))
            .RequirePermission(Permissions.Server.SettingsEdit);
        servers.MapPut("/{id:guid}/template", (Guid id, UpgradeTemplateRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.UpgradeTemplateAsync(new ServerId(id), request.TemplateId, ct)))
            .RequirePermission(Permissions.Server.Install);
        // Deletes the server's files: server.delete is needed too (checked by the change).
        servers.MapPost("/{id:guid}/game", (Guid id, ChangeGameRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.ChangeGameAsync(new ServerId(id), request.TemplateId, request.Variables, ct)))
            .RequirePermission(Permissions.Server.Install);
        // A backup of the server is copied into the new one: server.create on the node is needed too (checked by the clone).
        servers.MapPost("/{id:guid}/clone", (Guid id, CloneServerRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.CloneAsync(new ServerId(id), new NodeId(request.NodeId), request.Name, ct)))
            .RequirePermission(Permissions.Server.BackupsCreate);
        // Its old copy is deleted once it moved: server.backups.create, server.install and server.create on the node are
        // needed too (checked by the transfer).
        servers.MapPost("/{id:guid}/transfer", (Guid id, TransferServerRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.TransferAsync(new ServerId(id), new NodeId(request.NodeId), ct)))
            .RequirePermission(Permissions.Server.Delete);
        servers.MapPut("/{id:guid}/limits", (Guid id, ServerLimitsRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.SetLimitsAsync(new ServerId(id), request.CpuPercent, request.MemoryMb, request.Databases, ct)))
            .RequirePermission(Permissions.Server.LimitsEdit);
        servers.MapPost("/{id:guid}/update", (Guid id, UpdateGameRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.UpdateGameAsync(new ServerId(id), request.Validate, ct, request.SteamGuardCode)))
            .RequirePermission(Permissions.Server.Install);
        // Whoever hosts the server chooses whose Steam account downloads its game, as with its caps.
        servers.MapPut("/{id:guid}/steam-account", (Guid id, ServerSteamAccountRequest request, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.SetSteamAccountAsync(new ServerId(id), request.SteamAccountId, ct)))
            .RequirePermission(Permissions.Server.LimitsEdit);
        servers.MapPost("/{id:guid}/console", (Guid id, ConsoleCommandRequest request, ConsoleCommands console, CancellationToken ct) =>
                CommandAsync(console.SendAsync(new ServerId(id), request.Command, ct)))
            .RequirePermission(Permissions.Server.ConsoleWrite);
        // Making a player the game's admin also needs server.console.write (checked by the action).
        servers.MapPost("/{id:guid}/players", (Guid id, PlayerActionRequest request, ServerPlayers players, CancellationToken ct) =>
                CommandAsync(players.ActAsync(new ServerId(id), request.Action, request.Player, ct)))
            .RequirePermission(Permissions.Server.PlayersManage);
        servers.MapGet("/{id:guid}/players/lists", async (Guid id, ServerPlayers players, CancellationToken ct) => (await players.ListsAsync(new ServerId(id), ct)).ToHttp())
            .RequirePermission(Permissions.Server.PlayersManage);
        // Installs and updates need server.install, backups server.backups.create, the file manager's zips server.files.edit
        // (checked by the operation's type).
        servers.MapPost("/{id:guid}/operations/{operationId:guid}/cancel", (Guid id, Guid operationId, ServerManagement m, CancellationToken ct) =>
                DetailsAsync(m.CancelOperationAsync(new ServerId(id), operationId, ct)))
            .RequirePermission(Permissions.Server.Install, Permissions.Server.BackupsCreate, Permissions.Server.FilesEdit);

        // The accounts themselves: panel settings (their passwords are the panel's).
        var steamAccounts = api.MapGroup("/steam-accounts").WithTags("Servers");
        // Listed (never with a password) for whoever chooses a server's account too, by their names alone: a node's cached
        // login signs in with the user name, which is half of a credential.
        steamAccounts.MapGet("/", async (HttpContext http, SteamAccounts a, IPermissionEvaluator evaluator, CancellationToken ct) =>
            {
                var list = await a.ListAsync(ct);
                return TypedResults.Ok((await evaluator.GetAsync(http.User.GetRequiredUserId(), ct)).HasGlobal(Permissions.Settings.Manage)
                    ? list
                    : list with { Accounts = [.. list.Accounts.Select(account => account with { UserName = null })] });
            })
            .RequirePermissionAnywhere(Permissions.Settings.Manage, Permissions.Server.LimitsEdit);
        steamAccounts.MapPost("/", CreateSteamAccountAsync).RequirePermission(Permissions.Settings.Manage);
        steamAccounts.MapPut("/{accountId:guid}", async (Guid accountId, SteamAccountRequest request, SteamAccounts a, CancellationToken ct) =>
                (await a.UpdateAsync(accountId, request.ToDefinition(), ct)).ToHttp())
            .RequirePermission(Permissions.Settings.Manage);
        steamAccounts.MapDelete("/{accountId:guid}", async (Guid accountId, SteamAccounts a, CancellationToken ct) => (await a.DeleteAsync(accountId, ct)).ToHttp())
            .RequirePermission(Permissions.Settings.Manage);

        var templates = api.MapGroup("/templates").WithTags("Templates");
        templates.MapGet("/", async (TemplateCatalog catalog, CancellationToken ct) => TypedResults.Ok(await catalog.ListAsync(ct)))
            .RequirePermissionAnywhere(Permissions.Template.View, Permissions.Server.Create);
        templates.MapGet("/{id:guid}", async (Guid id, TemplateCatalog catalog, CancellationToken ct) => (await catalog.GetAsync(id, ct)).ToHttp())
            .RequirePermissionAnywhere(Permissions.Template.View, Permissions.Server.Create);
        templates.MapPost("/preview", async (TemplateContentRequest request, TemplateCatalog catalog, CancellationToken ct) =>
                TypedResults.Ok(await catalog.PreviewAsync(request.Content, ct)))
            .RequirePermission(Permissions.Template.Manage)
            .RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        templates.MapPost("/", ImportTemplateAsync).RequirePermission(Permissions.Template.Manage).RequireRateLimiting(WorkshopEndpoints.LookupRateLimitPolicy);
        return api;
    }

    private static async Task<Results<Created<SteamAccountView>, ProblemHttpResult>> CreateSteamAccountAsync(SteamAccountRequest request,
        SteamAccounts accounts, CancellationToken ct)
    {
        var result = await accounts.CreateAsync(request.ToDefinition(), ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/steam-accounts/{result.Value.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }

    private static async Task<Ok<IReadOnlyList<ServerSummary>>> ListAsync(Guid? nodeId, ServerManagement servers, CancellationToken ct) =>
        TypedResults.Ok(await servers.ListAsync(nodeId, ct));

    private static async Task<Results<Ok<ServerDetails>, ProblemHttpResult>> GetAsync(Guid id, ServerManagement servers, CancellationToken ct) =>
        (await servers.GetAsync(new ServerId(id), ct)).ToHttp();

    private static async Task<Results<Created<ServerDetails>, ProblemHttpResult>> CreateAsync(
        CreateServerRequest request, ServerManagement servers, CancellationToken ct)
    {
        var result = await servers.CreateAsync(new NodeId(request.NodeId), request.TemplateId, request.Name, request.Description, request.Variables,
            request.Ports, ct, request.PortNumbers);
        if (!result.IsSuccess)
        {
            return ApiProblems.FromDomainError(result.Error!);
        }

        return TypedResults.Created($"/api/v1/servers/{result.Value.Server.Id}", result.Value);
    }

    private static async Task<Results<Created<TemplateDetails>, ProblemHttpResult>> ImportTemplateAsync(
        TemplateContentRequest request, TemplateCatalog catalog, CancellationToken ct)
    {
        var result = await catalog.ImportAsync(request.Content, ct);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/templates/{result.Value.Template.Id}", result.Value)
            : ApiProblems.FromDomainError(result.Error!);
    }

    private static async Task<Results<Ok<AgentCommandResult>, ProblemHttpResult>> CommandAsync(Task<Result<AgentCommandResult>> command) =>
        (await command).ToHttp();

    private static async Task<Results<Ok<ServerDetails>, ProblemHttpResult>> DetailsAsync(Task<Result<ServerDetails>> change) =>
        (await change).ToHttp();
}
