using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>Errors of a server's mods from a catalog (Pulsar 1.0.9).</summary>
public static class ModErrors
{
    public static DomainError NotFound { get; } = new("mods.not_found", "That mod is not on the server.");

    public static DomainError Unsupported { get; } = new("mods.unsupported", "The server's game takes no mods from a catalog.");

    /// <summary>The game does, but not a server of its type (Mojang's own Minecraft server).</summary>
    public static DomainError TypeUnsupported { get; } =
        new("mods.type_unsupported", "A server of this type takes no mods or plugins: choose another type in its settings, then update it.");

    public static DomainError VersionUnknown { get; } =
        new("mods.version_unknown", "The server's game version is not known yet: install or update the server first.");

    /// <summary>A variable's list of a catalog's projects to look up is not one (Pulsar 1.0.14).</summary>
    public static DomainError ListInvalid { get; } = new("mods.list_invalid", "1 to 100 CurseForge project ids of a game, each a number.");

    /// <summary>The catalog's files run on an addon the server does not have (uMod's plugins on Oxide or Carbon; Pulsar 1.0.14).</summary>
    public static DomainError AddonMissing { get; } =
        new("mods.addon_missing", "These plugins run on an addon the server does not have: install it first, from this tab.");

    public static DomainError NoVersion { get; } =
        new("mods.no_version", "The project has no version for this server's game version and type.");

    public static DomainError FileInvalid { get; } =
        new("mods.file_invalid", "The project's file is not a .jar file the panel can put on the server.");

    public static DomainError CatalogBusy { get; } =
        new("mods.catalog_busy", "The mod catalog is receiving too many requests from this panel: try again in a minute.");

    public static DomainError ChangeRunning { get; } =
        new("mods.change_running", "The server's mods are already being changed: wait for that to end.");

    public static DomainError TooMany { get; } = new("mods.too_many", "A server has at most 300 mods, and a change takes at most 64 files.");

    public static DomainError QueryInvalid { get; } = new("mods.query_invalid", "A search is one line of up to 100 characters.");

    public static DomainError NoUpdates { get; } = new("mods.no_updates", "The catalog has nothing newer for those mods.");

    public static DomainError CurseForgeKeyInvalid { get; } =
        new("mods.curseforge_key_invalid", "A CurseForge API key is one line of up to 256 printable characters.");

    public static DomainError CurseForgeKeyRejected { get; } =
        new("mods.curseforge_key_rejected", "CurseForge does not accept that API key.");

    /// <summary>A catalog that needs a key the panel does not have (CurseForge: the administrator's).</summary>
    public static DomainError CatalogNotConfigured { get; } =
        new("mods.catalog_not_configured", "CurseForge needs an API key: an administrator sets it in the panel's settings.");

    /// <summary>The catalog has the file but its author lets it be downloaded from the catalog's own site only.</summary>
    public static DomainError DistributionForbidden(string name) =>
        new("mods.distribution_forbidden", $"{name}'s author lets it be downloaded from CurseForge's site only: download it there and upload it with the file manager.");

    public static DomainError RequirementMissing(string name) =>
        new("mods.requirement_missing", $"{name} requires a project that has no version for this server.");

    public static DomainError CatalogUnavailable(string detail) =>
        new("mods.catalog_unavailable", $"The mod catalog could not be asked: {detail}");

    public static DomainError RequiredBy(string name, string other) =>
        new("mods.required_by", $"{name} is needed by {other}: remove {other} first.");

    /// <summary>The node's code for a write that failed and could not all be undone: the files it was removing may be gone.</summary>
    public const string LeftPartialCode = "mods.left_partial";

    /// <summary>Two projects of one install need the same project in different versions.</summary>
    public static DomainError VersionConflict(string name, string version, string other) =>
        new("mods.version_conflict", $"{name} is needed in two versions, {version} and {other}: install them one at a time.");

    public static DomainError Incompatible(string name, string other) =>
        new("mods.incompatible", $"{name} does not work with {other}, which is on the server.");

    public static DomainError PathTaken(string path) =>
        new("mods.path_taken", $"Another mod on the server is already the file {path}.");
}
