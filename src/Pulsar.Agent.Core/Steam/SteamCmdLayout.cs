using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Steam;

/// <summary>
/// Where a server's SteamCMD keeps what it is made of and what it writes, on the node's platform (docs/architecture/06,
/// "SteamCMD"). Its copy is always in <see cref="Folder"/>. On Windows it is one program that keeps its data beside it
/// and updates itself with exit code 7. On Linux Valve's 32-bit program runs straight (not through
/// <c>steamcmd.sh</c>), keeps its data in <c>$HOME/Steam</c> (its home is a folder of the copy, so nothing of it is
/// left in the server's own folders), updates itself with exit code 42 (what <c>steamcmd.sh</c> restarts on), and
/// prints through pipes as it goes.
/// </summary>
/// <param name="ExecutableName">The program, relative to <see cref="Folder"/>.</param>
/// <param name="Home">Its home folder, relative to the server's folder; null: the server's own.</param>
/// <param name="DataFolder">Where it keeps its configuration (the cached login) and its cache, relative to the server's folder.</param>
/// <param name="SelfUpdateExitCode">The exit code of a run that only updated SteamCMD itself.</param>
/// <param name="ConsoleMode">How its output is read: Windows' SteamCMD buffers what it prints to a pipe.</param>
public sealed record SteamCmdLayout(string ExecutableName, string? Home, string DataFolder, int SelfUpdateExitCode, ConsoleMode ConsoleMode)
{
    /// <summary>The server's SteamCMD folder, relative to its directory.</summary>
    public const string Folder = ".pulsar/steamcmd";

    public static SteamCmdLayout Windows { get; } = new("steamcmd.exe", null, Folder, 7, ConsoleMode.Pty);

    public static SteamCmdLayout Linux { get; } = new("linux32/steamcmd", Folder + "/home", Folder + "/home/Steam", 42, ConsoleMode.Pipes);

    /// <summary>Every platform's lent login: an agent that stopped during a run left it there, whichever it is.</summary>
    public static IReadOnlyList<string> LentLogins { get; } = [Windows.LentLogin, Linux.LentLogin];

    /// <summary>The server's SteamCMD program, relative to its directory.</summary>
    public string Executable => Folder + "/" + ExecutableName;

    /// <summary>Where a Steam account's cached login is while it is lent to the server's SteamCMD, relative to the server's directory.</summary>
    public string LentLogin => DataFolder + "/" + SteamLoginStore.CachedLoginFile;

    /// <summary>The folder of <see cref="LentLogin"/>.</summary>
    public string LoginFolder => DataFolder + "/config";

    /// <summary>The app info SteamCMD cached: deleted before an update check, or it may print that instead of asking.</summary>
    public string AppInfoCache => DataFolder + "/appcache/appinfo.vdf";
}

/// <summary>A file of the verified bootstrapper, put in a server's SteamCMD copy.</summary>
/// <param name="Name">Its path relative to <see cref="SteamCmdLayout.Folder"/>.</param>
/// <param name="Source">The verified file the agent keeps.</param>
/// <param name="Executable">Whether it is a program (Linux sets its execute bits).</param>
public sealed record SteamCmdFile(string Name, string Source, bool Executable);
