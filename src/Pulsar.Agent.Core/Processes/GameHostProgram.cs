using System.Security.Cryptography;
using System.Text;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// The copy of the agent's program that host processes run from (docs/architecture/06, "Server hosts"): one folder per
/// version in <see cref="AgentPaths.HostProgramsDirectory"/>, inside the agent's state directory (SYSTEM and
/// Administrators only: hosts run as SYSTEM). Updating the agent replaces its installed files, which running hosts would
/// otherwise hold open; hosts of the previous version keep running from their copy until their games stop.
/// </summary>
public static class GameHostProgram
{
    private const string CompleteMarker = ".complete";

    /// <summary>
    /// The copy of <paramref name="programDirectory"/> (made now if there is none yet) and the path of
    /// <paramref name="executableName"/> in it.
    /// </summary>
    public static string Prepare(string programDirectory, string executableName, string hostProgramsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(programDirectory);
        var source = Path.GetFullPath(programDirectory);
        var target = Path.Combine(hostProgramsDirectory, Fingerprint(source));
        if (!File.Exists(Path.Combine(target, CompleteMarker)))
        {
            // Copied next to its final place and moved there whole: a host never starts from half a copy.
            var staging = target + ".tmp-" + RandomNumberGenerator.GetHexString(8, lowercase: true);
            Copy(source, staging);
            File.WriteAllText(Path.Combine(staging, CompleteMarker), string.Empty);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true); // an incomplete copy (the agent stopped while copying)
            }

            Directory.Move(staging, target);
        }

        return Path.Combine(target, executableName);
    }

    /// <summary>
    /// Deletes the copies no host runs from anymore (their programs are given in <paramref name="inUse"/>) and leftovers
    /// of interrupted copies. A copy still in use is never touched: a host loads parts of its program late.
    /// </summary>
    public static void CleanUp(string hostProgramsDirectory, string current, IEnumerable<string> inUse)
    {
        if (!Directory.Exists(hostProgramsDirectory))
        {
            return;
        }

        var keep = new HashSet<string>(inUse.Append(current).Select(p => Path.GetFullPath(Path.GetDirectoryName(p)!)), StringComparer.OrdinalIgnoreCase);
        var executable = Path.GetFileName(current);
        foreach (var directory in Directory.EnumerateDirectories(hostProgramsDirectory))
        {
            if (keep.Contains(Path.GetFullPath(directory)) || Running(Path.Combine(directory, executable)))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by a process that is not a host of this agent: tried again next time.
            }
        }
    }

    /// <summary>
    /// Whether a process may still run from the copy whose main executable is <paramref name="executable"/>, though no
    /// recorded host is known to: a recursive delete would remove its files up to the first one held open, and leave a
    /// host with half its program. A running image cannot be opened for writing.
    /// </summary>
    private static bool Running(string executable)
    {
        try
        {
            using var exclusive = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // an interrupted copy
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>A name for this version of the program: its files' names, sizes and times.</summary>
    internal static string Fingerprint(string programDirectory)
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(programDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            builder.Append(Path.GetRelativePath(programDirectory, file).ToUpperInvariant()).Append('|').Append(info.Length).Append('|')
                .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
