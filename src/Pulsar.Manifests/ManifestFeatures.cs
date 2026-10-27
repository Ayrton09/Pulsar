namespace Pulsar.Manifests;

/// <summary>
/// What a manifest uses that older agents do not read (those before Pulsar 1.0.5, 1.0.7, 1.0.8, 1.0.9, 1.0.11 or 1.0.12): they
/// refuse such a manifest, so the panel gives it only to agents that announce they read it.
/// </summary>
public static class ManifestFeatures
{
    /// <summary>
    /// The game settings of Pulsar 1.0.5: number variables, keys kept when empty, conditions on a variable that is
    /// neither a boolean nor a string, or the colon format.
    /// </summary>
    public static bool UsesGameSettings(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var types = manifest.VariableList.ToDictionary(v => v.Key, v => v.Type, StringComparer.Ordinal);
        return manifest.VariableList.Any(v => v.Type == VariableType.Number)
            || manifest.ConfigFileList.Any(f => f.KeepIfEmpty is { Count: > 0 } || f.Format == ConfigFileFormat.Colon)
            || manifest.Runtime.Values.SelectMany(r => Conditions(r.ArgumentList)).Any(c =>
                c.If.Split('.') is ["var", var key] && types.TryGetValue(key, out var type) && type is not (VariableType.Boolean or VariableType.String));
    }

    /// <summary>The configuration formats of Pulsar 1.0.12: <c>text</c> (a file that holds one value) and <c>xml</c> (elements' text).</summary>
    public static bool UsesTextConfig(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ConfigFileList.Any(f => f.Format is ConfigFileFormat.Text or ConfigFileFormat.Xml);
    }

    /// <summary>The configuration formats of Pulsar 1.0.7: <c>json</c> and <c>space</c>.</summary>
    public static bool UsesMoreConfigFormats(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ConfigFileList.Any(f => f.Format is ConfigFileFormat.Json or ConfigFileFormat.Space);
    }

    /// <summary>Minecraft: Bedrock Edition's install step (Pulsar 1.0.8).</summary>
    public static bool UsesMinecraftBedrock(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return (manifest.Install?.Steps ?? []).Concat(manifest.Update?.Steps ?? []).OfType<MinecraftBedrockStep>().Any();
    }

    /// <summary>
    /// Minecraft's server types of Pulsar 1.0.9 (<see cref="MinecraftServerStep.TypeVar"/>, its loader's version) and the
    /// conditions on what an enum's option maps, which its arguments use.
    /// </summary>
    public static bool UsesMinecraftTypes(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return (manifest.Install?.Steps ?? []).Concat(manifest.Update?.Steps ?? []).OfType<MinecraftServerStep>()
                .Any(s => s.TypeVar is not null || s.LoaderVar is not null)
            || manifest.Runtime.Values.SelectMany(r => Conditions(r.ArgumentList)).Any(c => c.If.Split('.') is ["var", _, _]);
    }

    /// <summary>The mod catalogs of Pulsar 1.0.9 (<see cref="GameManifest.Mods"/>).</summary>
    public static bool UsesMods(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ModList.Count > 0;
    }

    /// <summary>
    /// uMod's plugins (Pulsar 1.0.14): files in folders more than one name deep, ending otherwise than <c>.jar</c>, typed by
    /// the server's addon and changed while it runs.
    /// </summary>
    public static bool UsesUmod(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ModList.Any(s => s.Source == ModSourceDefinition.Umod);
    }

    /// <summary>
    /// A variable that lists a catalog's projects for the panel's editor, or a json file's array of ids (Pulsar 1.0.14:
    /// <c>curseForge</c>, <c>reforgerWorkshop</c>, a config file's <c>lists</c>).
    /// </summary>
    public static bool UsesCatalogLists(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.VariableList.Any(v => v.CurseForge is not null || v.ReforgerWorkshop is not null)
            || manifest.ConfigFileList.Any(f => f.Lists is { Count: > 0 });
    }

    /// <summary>Thunderstore's packages (Pulsar 1.0.14): the <c>thunderstore</c> addon source, and the catalog unpacked into BepInEx's folder.</summary>
    public static bool UsesThunderstore(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.AddonList.Any(a => a.Source is ThunderstoreSource) || manifest.ModList.Any(s => s.Source == ModSourceDefinition.Thunderstore);
    }

    /// <summary>A mod catalog whose modpacks install on the game (Pulsar 1.0.9, <see cref="ModSourceDefinition.Modpacks"/>).</summary>
    public static bool UsesModpacks(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ModList.Any(s => s.Modpacks is not null);
    }

    /// <summary>The console commands of Pulsar 1.0.9 (<see cref="GameManifest.Commands"/>).</summary>
    public static bool UsesCommands(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Commands is not null;
    }

    /// <summary>The commands for one player of Pulsar 1.0.11 (<see cref="CommandsDefinition.Players"/>).</summary>
    public static bool UsesPlayerCommands(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Commands?.Players is not null;
    }

    /// <summary>A Ctrl+C stop for a game run with pipes, which reaches the console the game opened (Pulsar 1.0.12).</summary>
    public static bool UsesPipesCtrlC(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Runtime.Values.Any(r => r.Console.Mode == ManifestConsoleMode.Pipes) && manifest.Stop.Steps.OfType<CtrlCStop>().Any();
    }

    /// <summary>An addon whose asset is a .tar.gz (Pulsar 1.0.12): older agents unpack only zips.</summary>
    public static bool UsesTarAddons(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.AddonList.Any(a => a.Source is GitHubReleaseSource github && AddonFormat(github) == "tar.gz");
    }

    /// <summary>A game server installed from a GitHub release (<see cref="GitHubReleaseStep"/>, Pulsar 1.0.12).</summary>
    public static bool UsesGitHubReleaseInstall(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return (manifest.Install?.Steps ?? []).Concat(manifest.Update?.Steps ?? []).OfType<GitHubReleaseStep>().Any();
    }

    /// <summary>A .NET runtime in the server's folder (<see cref="DotnetRuntimeStep"/>, Pulsar 1.0.12).</summary>
    public static bool UsesDotnetRuntime(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return (manifest.Install?.Steps ?? []).Concat(manifest.Update?.Steps ?? []).OfType<DotnetRuntimeStep>().Any();
    }

    /// <summary>The archive an addon's asset is, from the end of its pattern: "zip", "tar.gz", or null when it says neither.</summary>
    public static string? AddonFormat(GitHubReleaseSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Asset.EndsWith(@"\.zip$", StringComparison.OrdinalIgnoreCase) ? "zip"
            : source.Asset.EndsWith(@"\.tar\.gz$", StringComparison.OrdinalIgnoreCase) || source.Asset.EndsWith(@"\.tgz$", StringComparison.OrdinalIgnoreCase) ? "tar.gz"
            : null;
    }

    /// <summary>The game's user folders in the server's folder (<see cref="RuntimeDefinition.UserProfile"/>, Pulsar 1.0.12).</summary>
    public static bool UsesUserProfile(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Runtime.Values.Any(r => r.UserProfile is not null);
    }

    /// <summary>Several sections of one ini file, each declared on its own (Pulsar 1.0.12).</summary>
    public static bool UsesIniSections(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ConfigFileList.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
    }

    /// <summary>
    /// A game run with pipes whose console is its log file, or a log named by a pattern (Pulsar 1.0.12): older agents read a
    /// log only with RCON, and only by its name.
    /// </summary>
    public static bool UsesLogFileConsole(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Runtime.Values.Any(r => r.Console.LogFile is { } log && (r.Console.Mode == ManifestConsoleMode.Pipes || ManifestValidator.IsLogFilePattern(log)));
    }

    private static IEnumerable<ArgumentCondition> Conditions(IEnumerable<ArgumentTemplate> arguments) =>
        arguments.OfType<ArgumentCondition>().SelectMany(c => Conditions(c.Then).Concat(Conditions(c.Else ?? [])).Prepend(c));
}
