using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// The Linux agent's code, tested where it runs: on Linux (CI's Ubuntu runners, a WSL distribution). Elsewhere every
// test is skipped.
[assembly: SupportedOSPlatform("linux")]

namespace Pulsar.Agent.Linux.Tests;

/// <summary>A test of the Linux agent: skipped on other systems.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "The Linux agent is tested on Linux.";
        SkipUnless = nameof(LinuxOnly.IsLinux);
        SkipType = typeof(LinuxOnly);
    }
}

/// <summary>A theory of the Linux agent: skipped on other systems.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "The Linux agent is tested on Linux.";
        SkipUnless = nameof(LinuxOnly.IsLinux);
        SkipType = typeof(LinuxOnly);
    }
}

public static class LinuxOnly
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// A new empty folder for servers that game hosts run: outside /tmp and /var/tmp, which a host has of its own
    /// (<c>PrivateTmp</c>), as the agent's servers root is.
    /// </summary>
    public static string NewServersFolder()
    {
        var folder = Path.Combine("/var/lib/pulsar-linux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A new empty folder under the system's temporary folder (on Linux' own file system, never a Windows drive).</summary>
    public static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pulsar-linux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
