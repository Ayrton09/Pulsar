using System.Text.Json.Serialization;

namespace Pulsar.Manifests;

/// <summary>
/// A mod or mod framework the game supports (docs/architecture/04, "Addons"): where it comes from, where it goes in the
/// server and how it fits with the others. Installed and removed by the agent on request, never at the game's install.
/// </summary>
/// <param name="Key">Unique in the manifest: <c>^[a-z0-9][a-z0-9-]{1,31}$</c>.</param>
/// <param name="License">The addon's licence as its authors state it ("GPL-3.0", "zlib"), shown before installing.</param>
/// <param name="Url">The addon's home page (https), shown with it.</param>
/// <param name="Target">The folder of the server its archive is unpacked into ("" for the server's own folder).</param>
/// <param name="Requires">Addons that must be installed first.</param>
/// <param name="Conflicts">Addons that cannot be installed with it.</param>
/// <param name="ReinstallAfterUpdate">The game's updates overwrite it (Oxide replaces Rust's files): its latest release is installed again after every update.</param>
/// <param name="LineEdits">Lines the addon needs in the game's files, kept there before every start while it is installed.</param>
public sealed record AddonDefinition(
    string Key,
    string Name,
    string License,
    string Url,
    AddonSource Source,
    string? Description = null,
    string Target = "",
    IReadOnlyList<string>? Requires = null,
    IReadOnlyList<string>? Conflicts = null,
    bool ReinstallAfterUpdate = false,
    IReadOnlyList<LineEdit>? LineEdits = null)
{
    public IReadOnlyList<string> RequiresList => Requires ?? [];

    public IReadOnlyList<string> ConflictsList => Conflicts ?? [];

    public IReadOnlyList<LineEdit> LineEditList => LineEdits ?? [];
}

/// <summary>Where an addon's archive comes from: a closed set, like install steps.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(GitHubReleaseSource), "githubRelease")]
[JsonDerivedType(typeof(ThunderstoreSource), "thunderstore")]
public abstract record AddonSource;

/// <summary>
/// A zip asset of a GitHub release, checked against the SHA-256 GitHub publishes for it (an asset without one is
/// refused). The latest release, or the one with <see cref="Tag"/>; <see cref="Prerelease"/> takes the newest release
/// even when it is marked as a pre-release (Metamod:Source publishes its builds so).
/// </summary>
/// <param name="Repo"><c>owner/name</c>.</param>
/// <param name="Asset">A regular expression the asset's file name must match (one asset of the release).</param>
public sealed record GitHubReleaseSource(string Repo, string Asset, string? Tag = null, bool Prerelease = false) : AddonSource;

/// <summary>
/// Keeps <see cref="Line"/> right after the first line of <see cref="Path"/> that starts with <see cref="After"/> (both
/// compared without leading and trailing spaces, runs of spaces and tabs as one). Metamod's line in CS2's
/// <c>gameinfo.gi</c>, which the game's updates overwrite.
/// </summary>
/// <summary>
/// A Thunderstore package's newest version (Pulsar 1.0.14): a mod loader such as BepInEx's pack for Valheim, whose zip
/// holds it in a folder of its own beside Thunderstore's files about it. Thunderstore publishes no checksum: the agent
/// takes it over HTTPS from Thunderstore's own hosts only (docs/architecture/08, T52).
/// </summary>
/// <param name="Package">Its namespace and name: <c>denikson-BepInExPack_Valheim</c>.</param>
/// <param name="Folder">The zip's folder whose content is unpacked into the target (<c>BepInExPack_Valheim</c>); empty for the whole zip.</param>
public sealed record ThunderstoreSource(string Package, string Folder = "") : AddonSource;

public sealed record LineEdit(string Path, string After, string Line);
