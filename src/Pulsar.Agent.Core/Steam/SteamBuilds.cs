using System.Globalization;
using System.Text;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Steam;

/// <summary>Steam build ids: the one a server has installed, and the one Steam offers on a branch.</summary>
public static class SteamBuilds
{
    /// <summary>The branch SteamCMD installs without <c>-beta</c>.</summary>
    public const string PublicBranch = "public";

    // An app manifest is a few kilobytes: one far larger is not SteamCMD's.
    private const int MaxManifestSize = 1024 * 1024;

    /// <summary>The app manifest SteamCMD keeps in the server's folder, relative to it.</summary>
    public static string AppManifest(int appId) => $"steamapps/appmanifest_{appId.ToString(CultureInfo.InvariantCulture)}.acf";

    /// <summary>
    /// The build installed in a server's folder, from the app manifest SteamCMD keeps there; null when there is none
    /// or it does not name one. Read through the server's files (the folder is the server account's), with a size limit.
    /// </summary>
    public static string? Installed(IServerFiles files, string serverRoot, int appId)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var stream = files.OpenRead(serverRoot, AppManifest(appId));
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxManifestSize)
            {
                return null;
            }
        }

        var manifest = KeyValues.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        return BuildId(manifest?["AppState"]?["buildid"]?.Value);
    }

    /// <summary>The build Steam offers on <paramref name="branch"/>, from what <c>app_info_print</c> printed; null when it does not say.</summary>
    public static string? Latest(string appInfo, int appId, string branch)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        var app = KeyValues.FindBlock(appInfo, appId.ToString(CultureInfo.InvariantCulture));
        return BuildId(app?["depots"]?["branches"]?[branch]?["buildid"]?.Value);
    }

    /// <summary>A build id is a number; anything else is not one.</summary>
    private static string? BuildId(string? value) => value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit) ? value : null;
}
