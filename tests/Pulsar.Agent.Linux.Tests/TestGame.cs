using System.Runtime.CompilerServices;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>The agent's Linux build and the test game, copied beside the tests by the project.</summary>
public static class TestGame
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "testgame");

    /// <summary>The agent's program (<c>PulsarAgent</c>), which runs <c>game-exec</c>.</summary>
    public static string Agent => Path.Combine(AppContext.BaseDirectory, "agent", "PulsarAgent");

    /// <summary>The .NET the tests run on: where the framework-dependent builds find their runtime.</summary>
    public static string DotnetRoot => Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
        ? root
        : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location))))!;
}

/// <summary>Tests that make users and run games as them: root only (the agent's own rights), on Linux.</summary>
public sealed class LinuxRootFactAttribute : FactAttribute
{
    public LinuxRootFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Run as root on Linux (sudo), as the agent runs.";
        SkipUnless = nameof(IsRoot);
        SkipType = typeof(LinuxRootFactAttribute);
    }

    public static bool IsRoot => OperatingSystem.IsLinux() && Libc.geteuid() == 0;
}

/// <summary>The tests that make users run one after another.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RootTests
{
    public const string Name = "root";
}
