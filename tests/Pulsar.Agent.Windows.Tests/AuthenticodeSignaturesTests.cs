using System.Runtime.InteropServices;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>Who signed a file, as the Bedrock server's install checks it (1.0.8): Microsoft's signature on its program.</summary>
public sealed class AuthenticodeSignaturesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-authenticode-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [WindowsFact]
    public void AFileSignedByMicrosoftIsMicrosoftsAndNobodyElses()
    {
        var coreclr = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        var signatures = new AuthenticodeSignatures();

        Assert.True(signatures.IsSignedBy(coreclr, "Microsoft Corporation"));
        Assert.False(signatures.IsSignedBy(coreclr, "Microsoft"));
        Assert.False(signatures.IsSignedBy(coreclr, "Valve Corp."));
    }

    [WindowsFact]
    public void AModifiedOrUnsignedFileIsNobodys()
    {
        var modified = Path.Combine(_root, "bedrock_server.exe");
        File.Copy(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll"), modified);
        using (var stream = File.OpenWrite(modified))
        {
            stream.Seek(4096, SeekOrigin.Begin);
            stream.WriteByte(0x42); // breaks the signed hash
        }

        var unsigned = Path.Combine(_root, "unsigned.exe");
        File.WriteAllText(unsigned, "MZ not a signed program");

        Assert.False(new AuthenticodeSignatures().IsSignedBy(modified, "Microsoft Corporation"));
        Assert.False(new AuthenticodeSignatures().IsSignedBy(unsigned, "Microsoft Corporation"));
    }
}
