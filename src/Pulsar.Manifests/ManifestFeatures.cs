namespace Pulsar.Manifests;

/// <summary>
/// What a manifest uses that older agents do not read (those before Pulsar 1.0.5, or 1.0.7): they refuse such a
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

    /// <summary>The JSON configuration files of Pulsar 1.0.7.</summary>
    public static bool UsesJsonConfig(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ConfigFileList.Any(f => f.Format == ConfigFileFormat.Json);
    }

    private static IEnumerable<ArgumentCondition> Conditions(IEnumerable<ArgumentTemplate> arguments) =>
        arguments.OfType<ArgumentCondition>().SelectMany(c => Conditions(c.Then).Concat(Conditions(c.Else ?? [])).Prepend(c));
}
