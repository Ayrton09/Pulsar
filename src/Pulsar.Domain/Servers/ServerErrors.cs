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

    public static DomainError NotRunning { get; } = new("server.not_running", "The server is not running.");

    public static DomainError ConsoleInputInvalid { get; } =
        new("console.invalid_input", "A console command is one line of 1 to 512 characters, without control characters.");

    public static DomainError ExtraArgumentsInvalid { get; } = new("server.extra_arguments_invalid",
        $"At most {Server.MaxExtraArguments} extra arguments, each a single line of 1 to {Server.MaxExtraArgumentLength} characters.");

    public static DomainError ExtraArgumentsNotAllowed { get; } =
        new("server.extra_arguments_not_allowed", "This game does not allow extra arguments.");

    public static DomainError UpdateUnsupported { get; } = new("server.update_unsupported", "This game has no update steps.");

    public static DomainError NoFreeAllocation { get; } =
        new("server.no_free_allocation", "The node has no free allocation for one of the game's ports: add allocations to the node.");

    public static DomainError AllocationUnavailable { get; } =
        new("server.allocation_unavailable", "That allocation is not free on the server's node.");

    public static DomainError OperationNotFound { get; } = new("operation.not_found", "The operation does not exist.");

    public static DomainError OperationNotCancellable { get; } =
        new("operation.not_cancellable", "Only running installs and updates can be cancelled.");

    public static DomainError TemplateVersionExists { get; } =
        new("template.version_exists", "That game key and version are already registered: templates never change, give it a new version.");

    /// <summary>A manifest that does not pass validation; the message lists the problems.</summary>
    public static DomainError InvalidManifest(string message) => new("template.invalid", message);

    /// <summary>A value the manifest refuses; the message names the variable or port.</summary>
    public static DomainError InvalidSettings(string message) => new("server.invalid_settings", message);

    /// <summary>A variable only users who can edit server settings may change.</summary>
    public static DomainError VariableProtected(string key) =>
        new("server.variable_protected", $"Only users who can edit server settings can change '{key}'.");
}
