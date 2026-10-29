using Microsoft.AspNetCore.Http.HttpResults;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Http;

/// <summary>
/// ProblemDetails conventions: every error has a stable machine-readable <c>code</c> extension next to the
/// human-readable title, so the UI can translate it.
/// </summary>
public static class ApiProblems
{
    public const string CodeExtension = "code";

    public static ProblemHttpResult Create(int status, string code, string title, string? detail = null) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { [CodeExtension] = code });

    /// <summary>A business rule violation, with the status derived from its code.</summary>
    public static ProblemHttpResult FromDomainError(DomainError error) => Create(StatusFor(error.Code), error.Code, error.Message);

    /// <summary>
    /// <c>*.not_found</c> → 404; escalation and self-changes → 403 (the actor lacks the right to do it);
    /// conflicts with existing state → 409; file manager codes as HTTP names them (a stale version → 412, the node
    /// failing a transfer → 502); any other rule → 400.
    /// </summary>
    public static int StatusFor(string code) => code switch
    {
        _ when code.EndsWith(".not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
        "access.escalation" or "access.self_change" or "server.variable_protected" or "operation.forbidden" or "schedules.forbidden" or "server.create_forbidden"
            or "server.port_numbers_forbidden" or "server.game_updates_forbidden" or "players.admin_forbidden" or "server.steam_branch_forbidden"
            or "server.change_game_forbidden" or "server.clone_forbidden" or "server.transfer_forbidden"
            => StatusCodes.Status403Forbidden,
        "role.name_taken" or "role.in_use" or "access.last_administrator" or "role.system_immutable"
            or "node.name_taken" or "node.not_connected" or "node.has_servers" or "panel_key.rotation_in_progress" or "panel_key.not_retiring" or "panel_key.nodes_outdated"
            or "server.busy" or "server.must_be_stopped" or "server.not_installed" or "server.not_running" or "server.template_unsupported" or "server.template_not_newer" or "server.change_game_unsupported" or "server.copy_unsupported" or "server.copy_incomplete" or "server.clone_running" or "server.transferring" or "server.same_node" or "server.command_rejected"
            or "server.template_invalid"
            or "node.allocation_taken" or "node.allocation_in_use" or "server.no_free_allocation" or "server.allocation_unavailable"
            or "server.port_taken" or "server.port_not_allocated"
            or "template.version_exists" or "operation.not_cancellable" or "update.changed"
            or "files.unsupported" or "files.sftp_unsupported" or "files.archive_unsupported" or "files.exists" or "files.not_a_directory" or "files.is_a_directory" or "files.reparse_point" or "files.in_use" or "files.transfers_busy"
            or "backups.unsupported" or "backups.clean_unsupported" or "backups.clean_incomplete" or "server.limits_unsupported" or "backups.not_completed" or "backups.locked" or "backups.in_use" or "backups.corrupt"
            or "api_key.too_many" or "ssh_key.too_many" or "ssh_key.duplicate" or "enrollment_token.too_many" or "addon.unsupported" or "addon.requires" or "addon.conflicts" or "addon.required_by"
            or "schedules.too_many" or "schedules.sequence_running" or "schedules.sequence_over" or "server.update_check_unsupported"
            or "mods.change_running" or "mods.required_by" or "mods.incompatible" or "mods.version_conflict" or "mods.path_taken" or "mods.too_many"
            or "mods.catalog_not_configured" or "mods.distribution_forbidden" or "mods.addon_missing"
            or "notifications.too_many" or "backup_storage.in_use" or "backup_storage.too_many" or "backup_storage.holds_copies"
            or "database_host.in_use" or "database_host.too_many" or "database_host.name_taken" or "database.name_taken" or "database.limit_reached"
            or "steam_account.in_use" or "steam_account.too_many" or "steam_account.unsupported" or "steamcmd.account_required" or "steam_account.name_taken"
            or "server.steam_branch_unknown" or "server.steam_branch_password_required" or "server.steam_branch_unsupported" or "server.steam_branch_agent_too_old"
            => StatusCodes.Status409Conflict,
        "files.changed" => StatusCodes.Status412PreconditionFailed,
        "files.access_denied" or "files.outside_server" => StatusCodes.Status403Forbidden,
        "files.too_large" => StatusCodes.Status413PayloadTooLarge,
        "files.length_required" => StatusCodes.Status411LengthRequired,
        "files.disk_full" or "node.disk_low" => StatusCodes.Status507InsufficientStorage,
        "files.transfer_failed" or "files.failed" or "workshop.unavailable" or "database_host.unreachable" or "database.host_unreachable"
            or "mods.catalog_unavailable"
            => StatusCodes.Status502BadGateway,
        "mods.catalog_busy" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>Maps a result to 200 with the value, or to the problem of its error.</summary>
    public static Results<Ok<T>, ProblemHttpResult> ToHttp<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : FromDomainError(result.Error!);

    /// <summary>Maps a result to 204, or to the problem of its error.</summary>
    public static Results<NoContent, ProblemHttpResult> ToHttp(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : FromDomainError(result.Error!);
}
