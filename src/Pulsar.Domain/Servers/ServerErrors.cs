using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>Errors of the servers module. Codes are part of the API contract (ProblemDetails "code").</summary>
public static class ServerErrors
{
    public static DomainError NameInvalid { get; } =
        new("server.name_invalid", $"The server name is one line of 1 to {Server.MaxNameLength} characters.");

    public static DomainError DescriptionTooLong { get; } =
        new("server.description_too_long", $"The description must have at most {Server.MaxDescriptionLength} characters.");

    public static DomainError NotFound { get; } = new("server.not_found", "The server does not exist.");

    public static DomainError TemplateNotFound { get; } = new("server.template_not_found", "The game template does not exist.");

    public static DomainError TemplateUnsupported { get; } =
        new("server.template_unsupported", "The node's agent cannot run this game (update or reconfigure the agent).");

    public static DomainError Busy { get; } = new("server.busy", "Another operation is running on this server.");

    public static DomainError AddonUnknown { get; } = new("addon.not_found", "The server's game has no such addon.");

    public static DomainError AddonsUnsupported { get; } =
        new("addon.unsupported", "The node's agent cannot install addons (update the agent).");

    public static DomainError AddonRequires(string name, string other) =>
        new("addon.requires", $"{name} needs {other} installed first.");

    public static DomainError AddonConflicts(string name, string other) =>
        new("addon.conflicts", $"{name} cannot be installed with {other}: remove it first.");

    public static DomainError AddonRequiredBy(string name, string other) =>
        new("addon.required_by", $"{other} needs {name}: remove it first.");

    public static DomainError WorkshopInvalid { get; } =
        new("workshop.invalid", "Give a Steam app id and 1 to 100 Workshop ids (numbers).");

    public static DomainError WorkshopUnavailable(string reason) =>
        new("workshop.unavailable", $"Steam could not be asked about these Workshop items: {reason}");

    public static DomainError TemplateNotNewer { get; } =
        new("server.template_not_newer", "A server moves only to a newer version of its own game's template.");

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
        new("operation.not_cancellable", "Only running installs, updates and backups can be cancelled.");

    public static DomainError CreateForbidden { get; } = new("server.create_forbidden", "You may not create servers on this node.");

    public static DomainError OperationForbidden { get; } =
        new("operation.forbidden", "You may not cancel this kind of operation on this server.");

    public static DomainError BackupNotFound { get; } = new("backups.not_found", "The backup does not exist.");

    public static DomainError BackupNameInvalid { get; } =
        new("backups.name_invalid", $"A backup name is one line of 1 to {Backup.MaxNameLength} characters.");

    public static DomainError LimitsInvalid { get; } =
        new("server.limits_invalid", $"A CPU limit is 1 to 100 %, a memory limit {Server.MinMemoryLimitMb} MiB to {Server.MaxMemoryLimitMb} MiB.");

    public static DomainError LimitsUnsupported { get; } =
        new("server.limits_unsupported", "The node's agent cannot limit a server's CPU and memory (update the agent).");

    public static DomainError BackupLimitInvalid { get; } =
        new("backups.limit_invalid", $"Keep between 1 and {Server.MaxBackupLimit} backups, or set no limit.");

    public static DomainError BackupNotCompleted { get; } = new("backups.not_completed", "The backup is not complete.");

    public static DomainError BackupLocked { get; } = new("backups.locked", "The backup is locked: unlock it before deleting it.");

    public static DomainError BackupsUnsupported { get; } =
        new("backups.unsupported", "The node's agent cannot make backups (update the agent).");

    /// <summary>The agent refused a backup command; its code (<c>backups.*</c>) and message are passed on.</summary>
    public static DomainError BackupRejected(string code, string message) => new(code, message);

    public static DomainError TemplateVersionExists { get; } =
        new("template.version_exists", "That game key and version are already registered: templates never change, give it a new version.");

    public static DomainError FilesUnsupported { get; } =
        new("files.unsupported", "The node's agent has no file manager (update the agent).");

    public static DomainError FilePathInvalid(string message) => new("files.invalid_path", message);

    public static DomainError FileTooLarge { get; } = new("files.too_large", "The file is larger than allowed.");

    public static DomainError FileLengthRequired { get; } = new("files.length_required", "Uploads must declare their size (Content-Length).");

    public static DomainError FileTransferFailed { get; } =
        new("files.transfer_failed", "The transfer between the panel and the node did not complete.");

    /// <summary>The agent refused a file operation; its code (<c>files.*</c>) and message are passed on.</summary>
    public static DomainError FileRejected(string code, string message) => new(code, message);

    /// <summary>A manifest that does not pass validation; the message lists the problems.</summary>
    public static DomainError InvalidManifest(string message) => new("template.invalid", message);

    /// <summary>A value the manifest refuses; the message names the variable or port.</summary>
    public static DomainError InvalidSettings(string message) => new("server.invalid_settings", message);

    /// <summary>A variable only users who can edit server settings may change.</summary>
    public static DomainError VariableProtected(string key) =>
        new("server.variable_protected", $"Only users who can edit server settings can change '{key}'.");
}
