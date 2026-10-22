using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Agent.Windows.Servers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

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

/// <summary>A theory that runs only on Windows; elsewhere (the Linux CI leg) it is reported as skipped.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Windows only.";
        SkipUnless = nameof(WindowsFactAttribute.IsWindows);
        SkipType = typeof(WindowsFactAttribute);
    }
}

/// <summary>
/// A Job that kills what it holds when the test process ends, however it ends: host processes leave the test runner's
/// Job by design, so an aborted run would otherwise leave them (and their games) running.
/// </summary>
internal sealed unsafe class TestJob : IDisposable
{
    private readonly SafeFileHandle _handle = PInvoke.CreateJobObject(null, null);

    public TestJob()
    {
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (_handle.IsInvalid || !PInvoke.SetInformationJobObject((HANDLE)_handle.DangerousGetHandle(),
            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The test's Job could not be created.");
        }
    }

    public void Add(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (!PInvoke.AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed.");
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>A launcher whose hosts are in a <see cref="TestJob"/> before they start their game (which then joins it too).</summary>
internal sealed class JobbedHostLauncher(WindowsGameHostLauncher launcher, TestJob job) : IGameHostLauncher
{
    public GameHostReference Launch(Guid serverId, string pipe)
    {
        var host = launcher.Launch(serverId, pipe);
        job.Add(host.ProcessId);
        return host;
    }

    public bool IsAlive(GameHostReference host) => launcher.IsAlive(host);

    public bool Kill(GameHostReference host) => launcher.Kill(host);

    public bool Serves(PipeStream pipe, GameHostReference host) => launcher.Serves(pipe, host);

    public void CleanUpPrograms(IEnumerable<GameHostReference> running) => launcher.CleanUpPrograms(running);
}

/// <summary>Server accounts whose logon locks go to a folder of the tests (the agent's are in its state directory).</summary>
internal static class TestAccounts
{
    public static string LogonLocks { get; } = Path.Combine(Path.GetTempPath(), "pulsar-test-logon-locks");

    public static WindowsServerAccounts Create() => new(NullLogger<WindowsServerAccounts>.Instance, LogonLocks);
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
        new(TestAccounts.Create(), TimeProvider.System, NullLogger<WindowsGameProcessManager>.Instance, allowCurrentUser: true);

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
