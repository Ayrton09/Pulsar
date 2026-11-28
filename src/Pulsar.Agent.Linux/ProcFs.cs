using System.Globalization;
using System.Runtime.Versioning;

namespace Pulsar.Agent.Linux;

/// <summary>What <c>/proc</c> says about processes: who runs them, their session, start time and usage.</summary>
[SupportedOSPlatform("linux")]
public static class ProcFs
{
    /// <summary>Clock ticks per second of <c>/proc/&lt;pid&gt;/stat</c>'s times (USER_HZ, 100 on every Linux the agent runs on).</summary>
    public const int TicksPerSecond = 100;

    public static long PageSize { get; } = Environment.SystemPageSize;

    /// <summary>The fields of <c>/proc/&lt;pid&gt;/stat</c> the agent reads; null when the process is gone.</summary>
    public static ProcessStat? Stat(int pid)
    {
        string text;
        try
        {
            text = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return ParseStat(text);
    }

    /// <summary>
    /// Parses a <c>stat</c> line: the command name is in parentheses and may hold anything (spaces, parentheses), so the
    /// fields are read after its last closing parenthesis.
    /// </summary>
    public static ProcessStat? ParseStat(string text)
    {
        var close = text.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        // Fields from the 3rd (state) on.
        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 22)
        {
            return null;
        }

        long Field(int number) => long.TryParse(fields[number - 3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return new ProcessStat(
            State: fields[0][0],
            ProcessGroup: (int)Field(5),
            Session: (int)Field(6),
            UserTicks: Field(14),
            SystemTicks: Field(15),
            ChildrenUserTicks: Field(16),
            ChildrenSystemTicks: Field(17),
            StartTicks: Field(22),
            ResidentPages: Field(24));
    }

    /// <summary>
    /// What tells a process from another that later got its id: when it started, in clock ticks since boot, and which
    /// boot that was (<c>/proc/sys/kernel/random/boot_id</c>). Neither moves with the wall clock (a time sync, a resumed
    /// virtual machine), which the boot time <c>btime</c> does. Carried in a timestamp's ticks (the boot in the high bits,
    /// the start in the low 40, centuries of ticks): an identity to compare, not a time to show. Null when it is gone.
    /// </summary>
    public static DateTimeOffset? StartTime(int pid) => Stat(pid) is { State: not 'Z' } stat
        ? new DateTimeOffset(((long)BootHash() << 40) | (stat.StartTicks & ((1L << 40) - 1)), TimeSpan.Zero)
        : null;

    /// <summary>20 bits of this boot's id (random at each boot).</summary>
    private static int BootHash()
    {
        var id = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(id));
        return ((hash[0] << 12) | (hash[1] << 4) | (hash[2] >> 4)) & 0xFFFFF;
    }

    /// <summary>The program a process runs (its <c>/proc/&lt;pid&gt;/exe</c>); null when it is gone or cannot be read.</summary>
    public static string? ProgramOf(int pid)
    {
        try
        {
            return new FileInfo($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/exe").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every process (by id) <c>/proc</c> lists now.</summary>
    public static IEnumerable<int> All()
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                yield return pid;
            }
        }
    }

    /// <summary>The processes in <paramref name="session"/> (a game's tree: it leads a session of its own).</summary>
    public static IReadOnlyList<(int Pid, ProcessStat Stat)> InSession(int session) =>
        [.. All().Select(pid => (pid, Stat(pid))).Where(p => p.Item2 is { State: not 'Z' } s && s.Session == session).Select(p => (p.pid, p.Item2!))];

    /// <summary>The real user id of a process (<c>Uid:</c> of its status); null when it is gone.</summary>
    public static uint? UserOf(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var fields = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    return fields.Length > 0 && uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>The live processes whose real or effective user is <paramref name="uid"/>.</summary>
    public static IReadOnlyList<int> ProcessesOf(uint uid) =>
        [.. All().Where(pid => UserOf(pid) == uid && Stat(pid) is { State: not 'Z' })];
}

/// <summary>A process as <c>/proc/&lt;pid&gt;/stat</c> describes it (times in clock ticks, memory in pages).</summary>
public sealed record ProcessStat(char State, int ProcessGroup, int Session, long UserTicks, long SystemTicks, long ChildrenUserTicks,
    long ChildrenSystemTicks, long StartTicks, long ResidentPages);
