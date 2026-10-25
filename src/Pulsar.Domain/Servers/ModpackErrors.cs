using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>Errors of modpacks from a mod catalog (Pulsar 1.0.9).</summary>
public static class ModpackErrors
{
    public static DomainError Unsupported { get; } = new("modpack.unsupported", "The server's game takes no modpacks from a catalog.");

    public static DomainError NoVersion { get; } =
        new("modpack.no_version", "The modpack has no version for a server type this game runs.");

    /// <summary>A version made for loaders none of the game's server types runs (Quilt).</summary>
    public static DomainError LoaderUnsupported { get; } =
        new("modpack.loader_unsupported", "The modpack is made for a loader none of this game's server types runs.");

    public static DomainError GameVersionInvalid { get; } =
        new("modpack.game_version_invalid", "The modpack names no single game version the server takes.");

    public static DomainError FileInvalid { get; } =
        new("modpack.file_invalid", "The modpack's version has no .mrpack file the panel can have the node install.");
}
