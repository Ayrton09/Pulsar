using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

public sealed class SteamCmdBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-steamcmd-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => WindowsServerProvisioner.DeleteTree(_root);

    [WindowsFact]
    public void AFileSignedBySomeoneElseIsRefused()
    {
        // Signed by Microsoft: a valid signature, but not Valve's.
        var coreclr = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

        var error = Assert.Throws<GameProcessException>(() => SteamCmdBootstrapper.VerifyValveSignature(coreclr));

        Assert.Equal("steamcmd.untrusted", error.Code);
        Assert.Contains("not by Valve", error.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void AnUnsignedOrModifiedFileIsRefused()
    {
        Directory.CreateDirectory(_root);
        var fake = Path.Combine(_root, "steamcmd.exe");
        File.Copy(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll"), fake);
        using (var stream = File.OpenWrite(fake))
        {
            stream.Seek(4096, SeekOrigin.Begin);
            stream.WriteByte(0x42); // breaks the signed hash
        }

        var error = Assert.Throws<GameProcessException>(() => SteamCmdBootstrapper.VerifyValveSignature(fake));

        Assert.Contains("valid Authenticode signature", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Downloads SteamCMD from Valve (about 3 MB): CI, or a developer with <c>PULSAR_NETWORK_TESTS=1</c>.</summary>
    [NetworkFact]
    public async Task DownloadsAndVerifiesValvesSteamCmd()
    {
        using var bootstrapper = new SteamCmdBootstrapper(new AgentPaths(_root), NullLogger<SteamCmdBootstrapper>.Instance);

        var path = await bootstrapper.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_root, "steamcmd", "steamcmd.exe"), path);
        SteamCmdBootstrapper.VerifyValveSignature(path);
        Assert.Equal(path, await bootstrapper.GetAsync(TestContext.Current.CancellationToken)); // kept, not downloaded again
    }
}

public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Downloads from the internet: set PULSAR_NETWORK_TESTS=1.";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(NetworkFactAttribute);
    }

    public static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PULSAR_NETWORK_TESTS") == "1";
}
