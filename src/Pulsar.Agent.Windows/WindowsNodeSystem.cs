using System.Globalization;
using Microsoft.Win32;
using Pulsar.Agent.Core;
using Pulsar.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemInformation;

namespace Pulsar.Agent.Windows;

/// <summary>Inventory and node metrics from Windows (registry, GlobalMemoryStatusEx, GetSystemTimes).</summary>
public sealed class WindowsNodeSystem(TimeProvider clock) : INodeSystem
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private readonly Lock _lock = new();
    private CpuTimes _previous;

    public NodeInventory GetInventory(string? serversRoot) => new(
        Environment.MachineName,
        OsDescription(),
        OsBuild(),
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string cpu
            ? cpu.Trim() : null,
        Environment.ProcessorCount,
        (long)Memory().ullTotalPhys,
        serversRoot);

    public NodeMetrics SampleMetrics()
    {
        var memory = Memory();
        var current = CpuTimes.Read();
        double cpu;
        lock (_lock)
        {
            cpu = current.BusyPercentSince(_previous);
            _previous = current;
        }

        return new NodeMetrics(clock.GetUtcNow(), cpu, (long)(memory.ullTotalPhys - memory.ullAvailPhys), (long)memory.ullTotalPhys,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>"Windows 11 Pro 25H2" or "Windows Server 2022 Datacenter".</summary>
    internal static string OsDescription()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
        if (Environment.OSVersion.Version.Build >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
        {
            product = "Windows 11" + product["Windows 10".Length..];
        }

        return key?.GetValue("DisplayVersion") is string release && !product.Contains("Server", StringComparison.Ordinal)
            ? $"{product} {release}"
            : product;
    }

    /// <summary>"26200.6584": build and update revision.</summary>
    internal static string OsBuild()
    {
        var build = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        return key?.GetValue("UBR") is int revision ? $"{build}.{revision.ToString(CultureInfo.InvariantCulture)}" : build;
    }

    private static unsafe MEMORYSTATUSEX Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!PInvoke.GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception();
        }

        return status;
    }

    /// <summary>System-wide CPU times (100 ns units). Kernel time includes idle time.</summary>
    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User)
    {
        public static unsafe CpuTimes Read()
        {
            System.Runtime.InteropServices.ComTypes.FILETIME idle, kernel, user;
            if (!PInvoke.GetSystemTimes(&idle, &kernel, &user))
            {
                throw new System.ComponentModel.Win32Exception();
            }

            return new CpuTimes(Ticks(idle), Ticks(kernel), Ticks(user));
        }

        /// <summary>Busy share of all cores since <paramref name="previous"/> (since boot when there is none).</summary>
        public double BusyPercentSince(CpuTimes previous)
        {
            var total = Kernel - previous.Kernel + (User - previous.User);
            var idle = Idle - previous.Idle;
            return total == 0 ? 0 : Math.Round(100.0 * (total - idle) / total, 1);
        }

        private static ulong Ticks(System.Runtime.InteropServices.ComTypes.FILETIME time) => ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    }
}
