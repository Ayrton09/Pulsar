namespace Pulsar.Manifests;

/// <summary>
/// What a manifest uses that older agents do not read (those before Pulsar 1.0.5, 1.0.7, 1.0.8 or 1.0.9): they refuse such a
/// manifest, so the panel gives it only to agents that announce they read it.
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

    /// <summary>The console commands of Pulsar 1.0.9 (<see cref="GameManifest.Commands"/>).</summary>
    public static bool UsesCommands(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Commands is not null;
    }

    private static IEnumerable<ArgumentCondition> Conditions(IEnumerable<ArgumentTemplate> arguments) =>
        arguments.OfType<ArgumentCondition>().SelectMany(c => Conditions(c.Then).Concat(Conditions(c.Else ?? [])).Prepend(c));
}
