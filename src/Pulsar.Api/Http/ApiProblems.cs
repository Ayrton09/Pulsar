using Pulsar.Domain.Common;

namespace Pulsar.Api.Http;

/// <summary>
/// ProblemDetails conventions: every error has a stable machine-readable <c>code</c> extension next to the
/// human-readable title, so the UI can translate it.
/// </summary>
public static class ApiProblems
{
    public const string CodeExtension = "code";

    public static IResult Create(int status, string code, string title, string? detail = null) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { [CodeExtension] = code });

    /// <summary>A business rule violation (default 400; use 409 for conflicts with the current state).</summary>
    public static IResult FromDomainError(DomainError error, int status = StatusCodes.Status400BadRequest) =>
        Create(status, error.Code, error.Message);

    public static IResult NotFound(string code = "resource.not_found", string title = "The resource does not exist.") =>
        Create(StatusCodes.Status404NotFound, code, title);
}
