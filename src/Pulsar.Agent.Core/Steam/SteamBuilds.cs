using System.Globalization;
using System.Text;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

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

    /// <summary>
    /// The app's branches, from what <c>app_info_print</c> printed (Pulsar 1.0.14): those with a name SteamCMD takes after
    /// <c>-beta</c>, public first, <see cref="SteamBranch.MaxBranches"/> at most; descriptions lose control characters
    /// and are cut. Empty when it names none.
    /// </summary>
    public static IReadOnlyList<SteamBranch> Branches(string appInfo, int appId)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        var app = KeyValues.FindBlock(appInfo, appId.ToString(CultureInfo.InvariantCulture));
        var branches = (app?["depots"]?["branches"]?.Children ?? [])
            .Where(b => SteamBranch.IsName(b.Key))
            .DistinctBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
            .Select(b => new SteamBranch(b.Key, BuildId(b.Value["buildid"]?.Value), Description(b.Value["description"]?.Value),
                b.Value["pwdrequired"]?.Value == "1", Time(b.Value["timeupdated"]?.Value)))
            .OrderBy(b => b.Name == PublicBranch ? 0 : 1)
            .Take(SteamBranch.MaxBranches)
            .ToList();
        return branches;
    }

    private static string? Description(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = new string([.. text.Where(c => !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();
        return cleaned.Length == 0 ? null : cleaned.Length <= SteamBranch.MaxDescriptionLength ? cleaned : cleaned[..SteamBranch.MaxDescriptionLength];
    }

    private static DateTimeOffset? Time(string? seconds) =>
        long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 253402300800
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : null;

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
