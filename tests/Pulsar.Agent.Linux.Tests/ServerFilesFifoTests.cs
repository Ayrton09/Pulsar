using System.Diagnostics;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>
/// A named pipe the server's account left where the agent reads one of its files (a steamclient.so, an app manifest, a
/// configuration file): opening it would wait for a writer forever, so it is not opened.
/// </summary>
public sealed class ServerFilesFifoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-fifo-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [LinuxFact]
    public async Task ANamedPipeIsNotOpenedAsAFile()
    {
        using (var mkfifo = Process.Start("mkfifo", Path.Combine(_root, "steamclient.so")))
        {
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var read = Task.Run(() => LinuxServerFiles.OpenRegular(Path.Combine(_root, "steamclient.so")), TestContext.Current.CancellationToken);

        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        Assert.Throws<IOException>(() => read.GetAwaiter().GetResult());
    }

    [LinuxFact]
    public void AFileIsOpenedAndAMissingOneIsNull()
    {
        File.WriteAllText(Path.Combine(_root, "server.cfg"), "hostname x");

        using (var file = LinuxServerFiles.OpenRegular(Path.Combine(_root, "server.cfg")))
        {
            Assert.NotNull(file);
        }

        Assert.Null(LinuxServerFiles.OpenRegular(Path.Combine(_root, "missing.cfg")));
        Directory.CreateDirectory(Path.Combine(_root, "folder.cfg"));
        Assert.Null(LinuxServerFiles.OpenRegular(Path.Combine(_root, "folder.cfg")));
    }
}
