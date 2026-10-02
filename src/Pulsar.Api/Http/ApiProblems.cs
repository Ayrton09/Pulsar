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
    /// conflicts with existing state → 409; any other rule → 400.
    /// </summary>
    public static int StatusFor(string code) => code switch
    {
        _ when code.EndsWith(".not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
        "access.escalation" or "access.self_change" or "server.variable_protected" => StatusCodes.Status403Forbidden,
        "role.name_taken" or "role.in_use" or "access.last_administrator" or "role.system_immutable"
            or "node.name_taken" or "node.not_connected" or "node.has_servers" or "panel_key.rotation_in_progress" or "panel_key.not_retiring"
            or "server.busy" or "server.must_be_stopped" or "server.not_installed" or "server.not_running" or "server.template_unsupported" or "server.command_rejected"
            or "node.allocation_taken" or "node.allocation_in_use" or "server.no_free_allocation" or "server.allocation_unavailable"
            or "template.version_exists" or "operation.not_cancellable"
            => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>Maps a result to 200 with the value, or to the problem of its error.</summary>
    public static Results<Ok<T>, ProblemHttpResult> ToHttp<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : FromDomainError(result.Error!);

    /// <summary>Maps a result to 204, or to the problem of its error.</summary>
    public static Results<NoContent, ProblemHttpResult> ToHttp(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : FromDomainError(result.Error!);
}
