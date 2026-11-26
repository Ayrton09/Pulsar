using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulsar.Manifests;

/// <summary>
/// A manifest as one node reads it (docs/architecture/04, "Platforms"): the panel sends each agent only its own
/// platform's runtime and configuration files. On Windows that also leaves out what only Linux reads (an addon's Linux
/// asset and loader, a step's Windows-first install on Linux), so an agent released before the Linux agent, whose
/// parser refuses any member it does not know, still reads the manifest of a game that also runs on Linux.
/// </summary>
public static class ManifestPlatforms
{
    /// <summary>
    /// <paramref name="manifest"/> for a node of <paramref name="platform"/>; the text itself when it is not a JSON object
    /// or does not run there (the agent refuses it, as before).
    /// </summary>
    public static string For(string manifest, string platform)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(platform);
        JsonObject root;
        try
        {
            if (JsonNode.Parse(manifest) is not JsonObject parsed)
            {
                return manifest;
            }

            root = parsed;
        }
        catch (JsonException)
        {
            return manifest;
        }

        try
        {
            return Strip(root, platform) ?? manifest;
        }
        catch (InvalidOperationException)
        {
            // A member of another JSON type than a manifest's (the panel sends only manifests that parsed): left as it is.
            return manifest;
        }
    }

    private static string? Strip(JsonObject root, string platform)
    {
        if (root["runtime"] is not JsonObject runtime || !runtime.ContainsKey(platform))
        {
            return null;
        }

        foreach (var other in runtime.Select(r => r.Key).Where(key => key != platform).ToList())
        {
            runtime.Remove(other);
        }

        root["platforms"] = new JsonArray(platform);
        if (root["configFiles"] is JsonArray configFiles)
        {
            foreach (var file in configFiles.OfType<JsonObject>().ToList())
            {
                if (file["platforms"] is JsonArray platforms && !platforms.Any(p => p?.GetValue<string>() == platform))
                {
                    configFiles.Remove(file);
                }
                else
                {
                    file.Remove("platforms");
                }
            }
        }

        // A Linux node is given the step of its own app; no node reads the member itself.
        foreach (var section in (string[])["install", "update"])
        {
            foreach (var step in (root[section]?["steps"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (step["linuxAppId"] is { } linuxApp && platform == ManifestValidator.LinuxX64)
                {
                    step["appId"] = linuxApp.DeepClone();
                }

                step.Remove("linuxAppId");
            }
        }

        if (platform != ManifestValidator.LinuxX64)
        {
            foreach (var addon in (root["addons"] as JsonArray ?? []).OfType<JsonObject>())
            {
                addon.Remove("linux");
                (addon["source"] as JsonObject)?.Remove("linuxAsset");
            }

            foreach (var section in (string[])["install", "update"])
            {
                foreach (var step in (root[section]?["steps"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    step.Remove("windowsFirstOnLinux");
                    step.Remove("linuxAsset");
                }
            }
        }

        return root.ToJsonString();
    }
}
