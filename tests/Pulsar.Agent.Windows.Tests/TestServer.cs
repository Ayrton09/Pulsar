using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>Runs only on Windows; elsewhere (the Linux CI leg) it is reported as skipped.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Windows only.";
        SkipUnless = nameof(IsWindows);
        SkipType = typeof(WindowsFactAttribute);
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
}

/// <summary>A server directory with a copy of the test game server, deleted afterwards.</summary>
internal sealed class TestServerDirectory : IDisposable
{
    public const string Executable = "bin/Pulsar.TestGameServer.exe";

    public TestServerDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "pulsar-process-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(AppContext.BaseDirectory, "testgame");
        var bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }
    }

    public string Root { get; }

    public static WindowsGameProcessManager Manager() =>
        new(new WindowsServerAccounts(NullLogger<WindowsServerAccounts>.Instance), TimeProvider.System, NullLogger<WindowsGameProcessManager>.Instance);

    public ProcessLaunchSpec Spec(ConsoleMode mode, params string[] arguments) => new(Guid.NewGuid(), Root, Executable, arguments, "",
        new Dictionary<string, string>(), mode, ConsoleEncoding.Utf8, ProcessIdentity.CurrentUser);

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                // A killed process releases its executable a moment later.
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Collects a process's output in the background and waits for text to appear in it.</summary>
internal sealed class OutputCollector
{
    private readonly StringBuilder _text = new();
    private readonly Lock _lock = new();
    private readonly Task _pump;

    public OutputCollector(IGameProcess process)
    {
        _pump = Task.Run(async () =>
        {
            await foreach (var chunk in process.Output.ReadAllAsync())
            {
                lock (_lock)
                {
                    _text.Append(chunk.Text);
                }
            }
        });
    }

    public string Text
    {
        get
        {
            lock (_lock)
            {
                return _text.ToString();
            }
        }
    }

    /// <summary>Output is complete (the process and its console are gone).</summary>
    public Task Completed => _pump;

    /// <param name="occurrence">Waits for the n-th time the text appears (1 = the first).</param>
    public async Task<string> WaitForAsync(string expected, int timeoutSeconds = 30, int occurrence = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var text = Text;
            if (text.Split(expected).Length - 1 >= occurrence)
            {
                return text;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"'{expected}' did not appear in the output:\n{Text}");
    }
}
