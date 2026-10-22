using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>Windows lets only an elevated process (the agent runs as SYSTEM) create window stations.</summary>
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Needs an elevated process.";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(ElevatedFactAttribute);
    }

    public static bool Enabled => OperatingSystem.IsWindows() && WindowsDirectorySecurity.IsElevated();
}

/// <summary>
/// A server's window station is its own: another server's code (which runs in the same session) can neither guess its
/// name nor make one beforehand for the server to use.
/// </summary>
public sealed class ServerWindowStationTests
{
    private static readonly SecurityIdentifier s_me = WindowsIdentity.GetCurrent().User!;

    [ElevatedFact]
    public void AStationThatExistsAlreadyIsRefusedNotShared()
    {
        var name = $"Pulsar-test-{Guid.NewGuid():N}";
        using var first = ServerWindowStation.Create(name, s_me);

        // Opening it would give the server whatever permissions its maker set.
        var refused = Assert.Throws<Win32Exception>(() => ServerWindowStation.Create(name, s_me));
        Assert.True(refused.NativeErrorCode is 183 or 5, $"error {refused.NativeErrorCode}"); // ERROR_ALREADY_EXISTS, or ERROR_ACCESS_DENIED (CWF_CREATE_ONLY)
    }

    [ElevatedFact]
    public void EachStartOfAServerGetsAStationNobodyCouldName()
    {
        var server = Guid.NewGuid();
        using var first = ServerWindowStation.ForServer(server, s_me);
        using var second = ServerWindowStation.ForServer(server, s_me);

        Assert.StartsWith($"Pulsar-{server:N}-", first.DesktopName, StringComparison.Ordinal);
        Assert.NotEqual(first.DesktopName, second.DesktopName);
    }
}
