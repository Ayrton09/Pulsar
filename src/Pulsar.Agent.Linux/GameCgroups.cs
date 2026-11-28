using System.Globalization;
using System.Runtime.Versioning;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux;

/// <summary>
/// Cgroups (v2) for games: Linux' Job Objects (docs/architecture/06, "Limits"). Each game gets one of its own, under a
/// cgroup systemd delegated to the agent or to the game's host, with its CPU and memory caps; its usage and every process
/// of its tree are read there, and killing it kills them all.
/// </summary>
/// <param name="parent">The delegated cgroup folder (under <c>/sys/fs/cgroup</c>) the games' cgroups go in.</param>
[SupportedOSPlatform("linux")]
public sealed class GameCgroups(string parent)
{
    /// <summary>The scheduler's period for <c>cpu.max</c>: 100 ms, systemd's and the kernel's default.</summary>
    public const int CpuPeriodMicroseconds = 100_000;

    public string Parent { get; } = Path.TrimEndingDirectorySeparator(parent);

    /// <summary>The cgroup this process is in (<c>/proc/self/cgroup</c>'s v2 line), as a folder of <c>/sys/fs/cgroup</c>.</summary>
    public static string? Own()
    {
        foreach (var line in File.ReadLines("/proc/self/cgroup"))
        {
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                return "/sys/fs/cgroup" + line[3..].TrimEnd();
            }
        }

        return null;
    }

    /// <summary>
    /// Makes <paramref name="cgroup"/> able to hold games: this process moves into a leaf of its own (<paramref name="leaf"/>),
    /// since a cgroup whose controllers its children use may hold no process itself, and the CPU, memory and process
    /// controllers are given to its children.
    /// </summary>
    /// <exception cref="IOException">The cgroup was not delegated (systemd's <c>Delegate=yes</c>) or has no such controllers.</exception>
    public static GameCgroups Delegated(string cgroup, string leaf)
    {
        var own = Path.Combine(cgroup, leaf);
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "cgroup.procs"), "0\n");
        File.WriteAllText(Path.Combine(cgroup, "cgroup.subtree_control"), "+cpu +memory +pids\n");
        return new GameCgroups(cgroup);
    }

    /// <summary>The cgroup of a game, with its caps (none: not capped).</summary>
    /// <exception cref="IOException">It could not be made or capped.</exception>
    public GameCgroup Create(Guid serverId, ProcessLimits? limits)
    {
        var path = Path.Combine(Parent, $"game-{serverId:N}");
        if (Directory.Exists(path))
        {
            // Left by a game that ended without its cgroup going (an agent that died): empty, it goes; with processes
            // still in it, those are the server's and are ended first.
            var left = new GameCgroup(path);
            left.Kill();
            left.Dispose();
        }

        Directory.CreateDirectory(path);
        var cgroup = new GameCgroup(path);
        try
        {
            cgroup.ApplyLimits(limits);
            return cgroup;
        }
        catch
        {
            cgroup.Dispose();
            throw;
        }
    }
}

/// <summary>One game's cgroup: its caps, usage and tree.</summary>
[SupportedOSPlatform("linux")]
public sealed class GameCgroup(string path) : IDisposable
{
    private int _disposed;

    public string Path { get; } = path;

    public void ApplyLimits(ProcessLimits? limits)
    {
        var cpu = limits?.CpuPercent is { } percent
            ? $"{(long)GameCgroups.CpuPeriodMicroseconds * Environment.ProcessorCount * Math.Clamp(percent, 1, 100) / 100} {GameCgroups.CpuPeriodMicroseconds}"
            : $"max {GameCgroups.CpuPeriodMicroseconds}";
        Write("cpu.max", cpu);
        Write("memory.max", limits?.MemoryBytes is { } bytes ? bytes.ToString(CultureInfo.InvariantCulture) : "max");
        // Linux swaps instead of refusing memory: a cap that swap goes around would be none.
        Write("memory.swap.max", limits?.MemoryBytes is null ? "max" : "0", optional: true);
    }

    /// <summary>The processes in it now.</summary>
    public IReadOnlyList<int> Processes()
    {
        try
        {
            return [.. File.ReadAllLines(System.IO.Path.Combine(Path, "cgroup.procs"))
                .Select(l => int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0).Where(p => p > 0)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>CPU time of everything it ever ran, the memory it uses now, and its processes.</summary>
    public ProcessUsage Usage()
    {
        var cpu = TimeSpan.Zero;
        foreach (var line in ReadLines("cpu.stat"))
        {
            if (line.StartsWith("usage_usec ", StringComparison.Ordinal)
                && long.TryParse(line[11..], NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds))
            {
                cpu = TimeSpan.FromMicroseconds(microseconds);
            }
        }

        var memory = long.TryParse(ReadLines("memory.current").FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0;
        return new ProcessUsage(cpu, memory, Processes().Count, memory);
    }

    /// <summary>Whether something in it was refused memory because of its cap (<c>memory.events</c>: max or oom_kill).</summary>
    public bool ReachedMemoryLimit => ReadLines("memory.events").Any(line =>
        line.Split(' ') is [var name, var count] && name is "max" or "oom_kill" && count != "0");

    /// <summary>Kills every process in it at once (<c>cgroup.kill</c>, Linux 5.14+; one by one before that).</summary>
    public void Kill()
    {
        try
        {
            Write("cgroup.kill", "1");
        }
        catch (IOException)
        {
            foreach (var pid in Processes())
            {
                _ = Libc.kill(pid, Libc.SigKill);
            }
        }
    }

    /// <summary>Removes the cgroup once its processes are gone (killed first if need be).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                Directory.Delete(Path);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException)
            {
                // Busy: processes are still leaving it.
                if (attempt == 0)
                {
                    Kill();
                }

                Thread.Sleep(20);
            }
        }
    }

    private string[] ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(System.IO.Path.Combine(Path, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(string file, string value, bool optional = false)
    {
        var full = System.IO.Path.Combine(Path, file);
        if (optional && !File.Exists(full))
        {
            return;
        }

        File.WriteAllText(full, value + "\n");
    }
}
