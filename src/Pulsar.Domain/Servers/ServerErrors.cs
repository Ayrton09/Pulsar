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

    /// <summary>The server's game has no command for that (its template may have one in a newer version).</summary>
    public static DomainError PlayerActionUnsupported { get; } =
        new("players.unsupported", "This game cannot do that to a player from the panel.");

    public static DomainError PlayerInvalid { get; } = new("players.invalid",
        "A player is 1 to 64 letters, digits and _ - . (and spaces, if the game takes them), as the game names them.");

    /// <summary>A game's admin (Minecraft's operator) runs its commands in game, so making one is as much as a console command.</summary>
    public static DomainError ChangeGameForbidden { get; } = new("server.change_game_forbidden",
        "Changing the server's game deletes its files: it also needs the permission to delete the server.");

    public static DomainError CloneForbidden { get; } = new("server.clone_forbidden",
        "Cloning a server copies its files: it needs the permissions to back the server up and to see its files.");

    public static DomainError CloneRunning { get; } = new("server.clone_running",
        "A clone of this server, or onto that node, is still copying its files: wait until it ends.");

    public static DomainError OldCopyLeft { get; } = new("server.old_copy_left",
        "The server moved to the new node, but its copy on the old node could not be deleted: delete its folder and its Windows account (named after its short id) there by hand.");

    public static DomainError SameNode { get; } = new("server.same_node", "The server is already on that node.");

    public static DomainError Transferring { get; } = new("server.transferring",
        "The server is being moved to another node: wait until it ends.");

    public static DomainError TransferForbidden { get; } = new("server.transfer_forbidden",
        "Moving a server to another node needs the permissions to back it up, install it and delete it, and to create servers on that node.");

    public static DomainError CopyUnsupported { get; } = new("server.copy_unsupported",
        "A node has an agent too old to copy a server's files to another node: update it.");

    /// <summary>A clone's or a transfer's copy left out files (1.0.17): it is not restored, and a transfer leaves the server where it was.</summary>
    public static DomainError CopyIncomplete { get; } = new("server.copy_incomplete",
        "Files of the server were left out of the copy (held open by the running game): stop it and try again.");

    public static DomainError BackupCorrupt { get; } = new("server.backup_corrupt",
        "The node sent an archive of another size than the backup it made.");

    public static DomainError ChangeGameUnsupported { get; } = new("server.change_game_unsupported",
        "The server's node has an agent too old to change its game: update it.");

    public static DomainError PlayerAdminForbidden { get; } = new("players.admin_forbidden",
        "Making a player the game's admin, or taking it away, also needs the permission to send console commands.");

    public static DomainError ConsoleInputInvalid { get; } =
        new("console.invalid_input", "A console command is one line of 1 to 512 characters, without control characters.");

    public static DomainError ExtraArgumentsInvalid { get; } = new("server.extra_arguments_invalid",
        $"At most {Server.MaxExtraArguments} extra arguments, each a single line of 1 to {Server.MaxExtraArgumentLength} characters.");

    public static DomainError ExtraArgumentsNotAllowed { get; } =
        new("server.extra_arguments_not_allowed", "This game does not allow extra arguments.");

    public static DomainError UpdateUnsupported { get; } = new("server.update_unsupported", "This game has no update steps.");

    /// <summary>A Steam branch is chosen among those its node's last update check listed (Pulsar 1.0.14).</summary>
    public static DomainError SteamBranchUnknown { get; } = new("server.steam_branch_unknown",
        "That is not one of the game's Steam branches the last update check found: check for updates, then choose again.");

    /// <summary>Pulsar does not ask for, or keep, the passwords of Steam branches.</summary>
    public static DomainError SteamBranchPasswordRequired { get; } = new("server.steam_branch_password_required",
        "That Steam branch needs a password, which Pulsar does not install with.");

    /// <summary>Only a game downloaded with SteamCMD has branches.</summary>
    public static DomainError SteamBranchUnsupported { get; } = new("server.steam_branch_unsupported", "This game is not downloaded from Steam.");

    /// <summary>A branch change backs the server up, then updates it: whoever asks must be allowed both.</summary>
    public static DomainError SteamBranchForbidden { get; } = new("server.steam_branch_forbidden",
        "Changing the Steam branch needs the permissions to update the server and to back it up.");

    /// <summary>The node's agent is older than Pulsar 1.0.14 (it would install the template's branch whatever the server chose).</summary>
    public static DomainError SteamBranchAgentTooOld { get; } = new("server.steam_branch_agent_too_old",
        "The node's agent is too old to follow a Steam branch: update it first.");

    public static DomainError GameUpdatesInvalid { get; } = new("server.game_updates_invalid",
        $"Game updates are off, notified or installed, with at most {GameUpdateWatch.MaxWarningMinutes} minutes of warning.");

    /// <summary>The game's updates come from something no node can ask (a download of a fixed file, say).</summary>
    public static DomainError GameUpdatesUnsupported { get; } =
        new("server.game_updates_unsupported", "This game's updates cannot be checked.");

    /// <summary>Waiting until nobody plays needs to know who plays: the game's query.</summary>
    public static DomainError WhenEmptyUnsupported { get; } =
        new("server.when_empty_unsupported", "Pulsar cannot tell who plays this game, so it cannot wait until nobody does.");

    public static DomainError GameUpdatesForbidden { get; } = new("server.game_updates_forbidden",
        "Installing updates by themselves needs the permissions to update, stop and start the server (and to send console commands, to warn the players).");

    /// <summary>The node's agent does not check for game updates (older than Pulsar 1.0.9).</summary>
    public static DomainError GameUpdateCheckUnsupported { get; } =
        new("server.update_check_unsupported", "This node's agent cannot check for game updates: update Pulsar on it.");

    public static DomainError NoFreeAllocation { get; } =
        new("server.no_free_allocation", "The node has no free allocation for one of the game's ports: add allocations to the node.");

    public static DomainError AllocationUnavailable { get; } =
        new("server.allocation_unavailable", "That allocation is not free on the server's node.");

    public static DomainError PortTaken(int port) =>
        new("server.port_taken", $"Port {port} is used by another server of the node.");

    public static DomainError PortNumbersForbidden { get; } =
        new("server.port_numbers_forbidden", "You may not choose this node's ports by number: choose one of its allocations.");

    public static DomainError PortNotAllocated(int port) =>
        new("server.port_not_allocated", $"Port {port} is not one of the node's allocations: ask whoever manages the node to add it, or choose a free one.");

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

    public static DomainError DatabaseLimitInvalid { get; } =
        new("server.database_limit_invalid", $"The database limit is a number from 0 to {Server.MaxDatabaseLimit}.");

    public static DomainError BackupLimitInvalid { get; } =
        new("backups.limit_invalid", $"Keep between 1 and {Server.MaxBackupLimit} backups, or set no limit.");

    public static DomainError BackupNotCompleted { get; } = new("backups.not_completed", "The backup is not complete.");

    public static DomainError BackupLocked { get; } = new("backups.locked", "The backup is locked: unlock it before deleting it.");

    public static DomainError BackupsUnsupported { get; } =
        new("backups.unsupported", "The node's agent cannot make backups (update the agent).");

    public static DomainError CleanRestoreUnsupported { get; } =
        new("backups.clean_unsupported", "The node's agent cannot restore a backup clean (update the agent).");

    public static DomainError CleanRestoreIncomplete { get; } =
        new("backups.clean_incomplete", "This backup left out files the game held open: a clean restore would delete them. Restore it without cleaning.");

    /// <summary>The agent refused a backup command; its code (<c>backups.*</c>) and message are passed on.</summary>
    public static DomainError BackupRejected(string code, string message) => new(code, message);

    public static DomainError TemplateVersionExists { get; } =
        new("template.version_exists", "That game key and version are already registered: templates never change, give it a new version.");

    /// <summary>The node's agent predates the file manager's zips (Pulsar 1.0.11).</summary>
    public static DomainError ArchivesUnsupported { get; } =
        new("files.archive_unsupported", "This node's agent cannot zip or unzip files: update Pulsar on it.");

    public static DomainError FilesUnsupported { get; } =
        new("files.unsupported", "The node's agent has no file manager (update the agent).");

    /// <summary>The node's agent predates SFTP (it does not announce <c>server.files_sftp</c>).</summary>
    public static DomainError FilesSftpUnsupported { get; } =
        new("files.sftp_unsupported", "SFTP needs a newer agent on this server's node (update the agent).");

    /// <summary>A download asked to start before the beginning of the file.</summary>
    public static DomainError FileOffsetInvalid { get; } = new("files.invalid_offset", "A download cannot start before the beginning of the file.");

    public static DomainError FilePathInvalid(string message) => new("files.invalid_path", message);

    public static DomainError FileTooLarge { get; } = new("files.too_large", "The file is larger than allowed.");

    public static DomainError FileLengthRequired { get; } = new("files.length_required", "Uploads must declare their size (Content-Length).");

    public static DomainError FileTransferFailed { get; } =
        new("files.transfer_failed", "The transfer between the panel and the node did not complete.");

    /// <summary>The node, or the user on it, has as many file transfers running as it may (FileTransferLimits).</summary>
    public static DomainError FileTransfersBusy { get; } =
        new("files.transfers_busy", "Too many file transfers are running on this node. Try again when one ends.");

    /// <summary>The agent refused a file operation; its code (<c>files.*</c>) and message are passed on.</summary>
    public static DomainError FileRejected(string code, string message) => new(code, message);

    /// <summary>A manifest that does not pass validation; the message lists the problems.</summary>
    public static DomainError InvalidManifest(string message) => new("template.invalid", message);

    /// <summary>A stored template valid when it was imported that this panel's checks refuse (they may have become stricter).</summary>
    public static DomainError TemplateNoLongerValid(string key, string version) => new("server.template_invalid",
        $"The game template {key} {version} does not pass this panel's checks anymore: move its servers to a newer version of it.");

    /// <summary>A value the manifest refuses; the message names the variable or port.</summary>
    public static DomainError InvalidSettings(string message) => new("server.invalid_settings", message);

    /// <summary>A variable only users who can edit server settings may change.</summary>
    public static DomainError VariableProtected(string key) =>
        new("server.variable_protected", $"Only users who can edit server settings can change '{key}'.");
}
