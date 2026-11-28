using System.Runtime.Versioning;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Wine on this machine, which runs a game's Windows program on Linux (<c>runtime.linux-x64.wine</c>,
/// docs/architecture/06): its 64-bit loader, an ELF program the distribution installed, found in the places packages put
/// it. Never one a server's user could change: root's, in a folder only root can write to.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxWine
{
    /// <summary>
    /// Where the packages put Wine's loader: Debian's and Ubuntu's <c>wine64</c> (<c>/usr/lib/wine/wine64</c>), other
    /// distributions' and Wine 10's single <c>wine</c>, and WineHQ's own packages under <c>/opt</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> Candidates =
    [
        "/usr/lib/wine/wine64",
        "/usr/bin/wine64",
        "/usr/bin/wine",
        "/opt/wine-stable/bin/wine64",
        "/opt/wine-stable/bin/wine",
    ];

    /// <summary>The prefix of a server's games, in its own folder: Pulsar's, never backed up, never shown.</summary>
    public static string PrefixOf(string serverRoot) => Path.Combine(serverRoot, ".pulsar", "wine");

    /// <summary>
    /// The registry a new prefix starts with: Wine's null graphics driver, so a game's window (Unity's batch-mode one,
    /// say) is made without a display, and nothing ever tries to reach one.
    /// </summary>
    public const string UserRegistry = """
        WINE REGISTRY Version 2
        ;; All keys relative to REGISTRY\\User\\S-1-5-21-0-0-0-1000

        #arch=win64

        [Software\\Wine\\Drivers]
        "Graphics"="null"

        """;

    /// <summary>
    /// What Wine is told besides the prefix: no debug output (it would flood the console); no prompt to install Wine's own
    /// .NET and browser engine, which nobody could answer; and no debugger for a program that faults (Wine starts
    /// <c>winedbg</c>, which holds the program until someone answers it: the game would never exit, nor be seen to
    /// crash). The game's manifest may say otherwise.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WINEDEBUG"] = "-all",
        ["WINEDLLOVERRIDES"] = "mscoree,mshtml=;winedbg.exe=d",
    };

    /// <summary>The loader, or null when Wine is not installed.</summary>
    public static string? Find() => Candidates.FirstOrDefault(IsTrusted);

    /// <summary>A regular ELF file of root's, which nobody else may change, in folders nobody else may change either.</summary>
    internal static bool IsTrusted(string path)
    {
        if (Libc.Lstat(path) is not { IsRegularFile: true } stat || !OnlyRootWrites(stat))
        {
            return false;
        }

        for (var folder = Path.GetDirectoryName(path); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (Libc.Lstat(folder) is not { IsDirectory: true } folderStat || !OnlyRootWrites(folderStat))
            {
                return false;
            }
        }

        return GameExec.CheckProgram(path) is null;
    }

    // Owned by root, not writable by its group or others.
    private static bool OnlyRootWrites(Libc.Stat stat) => stat.Uid == 0 && (stat.Permissions & 0x12) == 0;
}
