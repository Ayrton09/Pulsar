using System.Globalization;
using System.Runtime.Versioning;
using Pulsar.Agent.Core;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Linux;

/// <summary>The Linux machine, as reported to the panel: read from <c>/proc</c> and <c>/etc/os-release</c>.</summary>
/// <param name="root">Where <c>/proc</c> and <c>/etc</c> are (tests read a copy).</param>
[SupportedOSPlatform("linux")]
public sealed class LinuxNodeSystem(TimeProvider clock, string root = "/") : INodeSystem
{
    private readonly Lock _lock = new();
    private CpuTimes _previous;

    public NodeInventory GetInventory(string? serversRoot) => new(
        Environment.MachineName,
        OsDescription(),
        OsBuild(),
        CpuModel(),
        Environment.ProcessorCount,
        MemoryInfo().Total,
        serversRoot,
        ManifestValidator.LinuxX64);

    public NodeMetrics SampleMetrics()
    {
        var memory = MemoryInfo();
        var current = CpuTimes.Parse(Read("proc/stat"));
        double cpu;
        lock (_lock)
        {
            cpu = current.BusyPercentSince(_previous);
            _previous = current;
        }

        return new NodeMetrics(clock.GetUtcNow(), cpu, memory.Total - memory.Available, memory.Total, Uptime());
    }

    /// <summary>"Ubuntu 24.04.3 LTS": the distribution's own name for itself.</summary>
    internal string OsDescription()
    {
        var release = TryRead("etc/os-release") ?? TryRead("usr/lib/os-release");
        return release is not null && OsReleaseValue(release, "PRETTY_NAME") is { Length: > 0 } name ? name : "Linux";
    }

    /// <summary>The kernel's release: "6.8.0-48-generic".</summary>
    internal string OsBuild() => TryRead("proc/sys/kernel/osrelease")?.Trim() is { Length: > 0 } kernel ? kernel : Environment.OSVersion.Version.ToString();

    internal string? CpuModel()
    {
        foreach (var line in (TryRead("proc/cpuinfo") ?? string.Empty).Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim() == "model name")
            {
                return line[(colon + 1)..].Trim() is { Length: > 0 } model ? model : null;
            }
        }

        return null;
    }

    /// <summary>Total and available memory (<c>MemAvailable</c>: what can be had without swapping), in bytes.</summary>
    internal (long Total, long Available) MemoryInfo()
    {
        long total = 0, available = 0;
        foreach (var line in Read("proc/meminfo").Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes))
            {
                switch (parts[0])
                {
                    case "MemTotal:":
                        total = kibibytes * 1024;
                        break;
                    case "MemAvailable:":
                        available = kibibytes * 1024;
                        break;
                }
            }
        }

        return (total, Math.Min(available, total));
    }

    internal TimeSpan Uptime()
    {
        var first = Read("proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    /// <summary>A value of os-release (<c>KEY="value"</c>, quotes and their escapes as the format has them).</summary>
    internal static string? OsReleaseValue(string content, string key)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(key + "=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[(key.Length + 1)..];
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            {
                value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            }

            return value.Trim();
        }

        return null;
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(root, path));

    private string? TryRead(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The first line of <c>/proc/stat</c>: every CPU's time, in clock ticks, by kind.</summary>
    internal readonly record struct CpuTimes(ulong Idle, ulong Total)
    {
        public static CpuTimes Parse(string stat)
        {
            var line = stat.Split('\n')[0];
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || fields[0] != "cpu")
            {
                return default;
            }

            // user nice system idle iowait irq softirq steal (guest time is already in user and nice).
            var values = fields.Skip(1).Take(8).Select(f => ulong.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
            var idle = values[3] + (values.Length > 4 ? values[4] : 0);
            return new CpuTimes(idle, values.Aggregate(0UL, (sum, v) => sum + v));
        }

        /// <summary>Busy share of all cores since <paramref name="previous"/> (since boot when there is none).</summary>
        public double BusyPercentSince(CpuTimes previous)
        {
            var total = Total - previous.Total;
            var idle = Idle - previous.Idle;
            return total == 0 || idle > total ? 0 : Math.Round(100.0 * (total - idle) / total, 1);
        }
    }
}
