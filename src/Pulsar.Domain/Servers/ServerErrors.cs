using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>Errors of the servers module. Codes are part of the API contract (ProblemDetails "code").</summary>
public static class ServerErrors
{
    public static DomainError NameInvalid { get; } =
        new("server.name_invalid", $"The server name must have between 1 and {Server.MaxNameLength} characters.");

    public static DomainError DescriptionTooLong { get; } =
        new("server.description_too_long", $"The description must have at most {Server.MaxDescriptionLength} characters.");

    public static DomainError NotFound { get; } = new("server.not_found", "The server does not exist.");

    public static DomainError TemplateNotFound { get; } = new("server.template_not_found", "The game template does not exist.");

    public static DomainError TemplateUnsupported { get; } =
        new("server.template_unsupported", "The node's agent cannot run this game (update or reconfigure the agent).");

    public static DomainError Busy { get; } = new("server.busy", "Another operation is running on this server.");

    public static DomainError NotInstalled { get; } = new("server.not_installed", "The server is not installed.");

    public static DomainError MustBeStopped { get; } = new("server.must_be_stopped", "Stop the server first.");

    public static DomainError CommandRejected { get; } = new("server.command_rejected", "The node's agent rejected the command.");
}
