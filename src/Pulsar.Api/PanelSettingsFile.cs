using Microsoft.Extensions.Configuration.Json;

namespace Pulsar.Api;

/// <summary>
/// The panel's settings file outside its program folder, named by <see cref="Variable"/>: on Linux the installer keeps
/// the settings in /etc/pulsar/panel/appsettings.json (docs/guides/install.md, "Linux"), which an update of the
/// programs never replaces. It is read right after appsettings.{Environment}.json, so environment variables and the
/// command line still override it as they override those files.
/// </summary>
internal static class PanelSettingsFile
{
    public const string Variable = "PULSAR_SETTINGS_FILE";

    /// <summary>Adds the file at <paramref name="path"/> (none when null or blank): an absolute path, of a file that must exist.</summary>
    public static void Add(IConfigurationBuilder configuration, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new PanelStartupException($"{Variable} must be an absolute path, not '{path}'.");
        }

        if (!File.Exists(path))
        {
            throw new PanelStartupException($"The settings file {path} ({Variable}) does not exist.");
        }

        var source = new JsonConfigurationSource { Path = path, Optional = false, ReloadOnChange = false };
        source.ResolveFileProvider();

        var sources = configuration.Sources;
        var last = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                last = i;
            }
        }

        sources.Insert(last + 1, source);
    }
}
