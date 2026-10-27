// A fake game server for the agent's tests and the `pulsar-test` manifest (docs/architecture/06, "Tests"). It behaves
// like the real ones where it matters to the agent: console output (stdout, stderr and the console API), commands on
// its input, a readiness line, child and grandchild processes, crashes, and refusals to stop.
//
// Arguments:
//   --ready-after <ms>        print "Test server ready" after a delay (default 0)
//   --ignore-quit             answer "quit" without exiting (exercises the stop timeout)
//   --ignore-interrupt        survive Ctrl+C
//   --exit-after <ms> <code>  exit by itself with <code> after a delay (a crash when <code> is not 0)
//   --print-args              print every argument between brackets (argv round trip)
// Commands (one per line): quit, crash <code>, say <text>, conout <text>, spawn, whoami, env <name>, read <path>,
//   write <path>, handle <value>, privileges, allocate <MB>, burn <seconds>, save-off, save, save-on (a world saved to
//   world.dat, like Minecraft's), lock <path> (a file held open with no sharing, like a game's world).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

if (args is ["--sleep"])
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

if (args is ["--child"])
{
    // A child that starts a grandchild: killing the tree must take both.
    using var grandchild = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--sleep") { UseShellExecute = false });
    Console.WriteLine($"Test server: grandchild {grandchild?.Id}");
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

var readyAfter = 0;
var ignoreQuit = false;
var ignoreInterrupt = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ready-after":
            readyAfter = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--ignore-quit":
            ignoreQuit = true;
            break;
        case "--ignore-interrupt":
            ignoreInterrupt = true;
            break;
        case "--exit-after":
            var delay = int.Parse(args[++i], CultureInfo.InvariantCulture);
            var code = int.Parse(args[++i], CultureInfo.InvariantCulture);
            _ = Task.Delay(delay).ContinueWith(_ =>
            {
                Console.WriteLine($"Test server: exiting by itself with {code}");
                Environment.Exit(code);
            }, TaskScheduler.Default);
            break;
        case "--print-args":
            foreach (var argument in args)
            {
                Console.WriteLine($"arg[{argument}]");
            }

            break;
    }
}

var kept = new List<nint>(); // memory taken by "allocate"
var held = new List<FileStream>(); // files taken by "lock"
var saves = 0;
using var interrupted = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (ignoreInterrupt)
    {
        Console.WriteLine("Test server: ignoring Ctrl+C");
        return;
    }

    Console.WriteLine("Test server: interrupted");
    interrupted.Set();
    if (Console.IsInputRedirected)
    {
        // A read from a pipe does not end on Ctrl+C as a console's does.
        _ = Task.Run(() => Environment.Exit(130));
    }
};

Console.WriteLine($"Test server starting (pid {Environment.ProcessId})");
Console.Error.WriteLine("Test server: hello from stderr");
_ = Task.Delay(readyAfter).ContinueWith(_ => Console.WriteLine("Test server ready"), TaskScheduler.Default);

string? line;
while ((line = Console.ReadLine()) is not null)
{
    var command = line.Trim();
    var space = command.IndexOf(' ', StringComparison.Ordinal);
    var name = space < 0 ? command : command[..space];
    var argument = space < 0 ? "" : command[(space + 1)..];
    switch (name)
    {
        case "quit" when ignoreQuit:
            Console.WriteLine("Test server: ignoring quit");
            break;
        case "quit":
            Console.WriteLine("Test server: quitting");
            return 0;
        case "crash":
            Console.WriteLine($"Test server: crashing with {argument}");
            return int.Parse(argument, CultureInfo.InvariantCulture);
        case "say":
            Console.WriteLine($"Test server: {argument}");
            break;
        case "conout":
            WriteToConsoleBuffer(argument);
            break;
        case "spawn":
            using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child") { UseShellExecute = false }))
            {
                Console.WriteLine($"Test server: child {child?.Id}");
            }

            break;
        case "allocate":
            // Commits (and touches) the given MB of native memory, as a game engine does, and keeps it; past a memory
            // limit the allocation fails and the OutOfMemoryException, unhandled, ends the process. (Not managed
            // memory: .NET's garbage collector reads a job's limit and stays under it.)
            for (var mb = 0; mb < int.Parse(argument, CultureInfo.InvariantCulture); mb += 16)
            {
                var block = Marshal.AllocHGlobal(16 * 1024 * 1024);
                unsafe
                {
                    new Span<byte>((void*)block, 16 * 1024 * 1024).Fill(1);
                }

                kept.Add(block);
            }

            Console.WriteLine($"Test server: allocated {argument} MB");
            break;
        case "burn":
            // Every processor of the machine busy for the given seconds. (Not Environment.ProcessorCount: .NET counts
            // the share of the machine a CPU cap allowed when the process started.)
            var until = Stopwatch.StartNew();
            var seconds = double.Parse(argument, CultureInfo.InvariantCulture);
            var processors = int.Parse(Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS") ?? "1", CultureInfo.InvariantCulture);
            var burners = Enumerable.Range(0, processors).Select(_ => new Thread(() =>
            {
                while (until.Elapsed.TotalSeconds < seconds)
                {
                    Thread.SpinWait(1000);
                }
            })).ToList();
            burners.ForEach(t => t.Start());
            burners.ForEach(t => t.Join());
            Console.WriteLine("Test server: burned");
            break;
        case "save-off":
            Console.WriteLine("Test server: autosave off");
            break;
        case "save":
            Try(() =>
            {
                File.WriteAllText("world.dat", $"save {++saves}"); // in its working directory (the pulsar-test manifest's data)
                Console.WriteLine($"Test server: saved ({saves})");
            });
            break;
        case "save-on":
            Console.WriteLine("Test server: autosave on");
            break;
        case "lock":
            Try(() =>
            {
                var file = new FileStream(argument, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                file.Write("held by the game"u8);
                file.Flush(flushToDisk: true);
                held.Add(file);
                Console.WriteLine($"Test server: locked {argument}");
            });
            break;
        case "whoami":
            Console.WriteLine($"Test server: user={WindowsIdentity.GetCurrent().Name}");
            break;
        case "env":
            Console.WriteLine($"Test server: {argument}={Environment.GetEnvironmentVariable(argument)}");
            break;
        case "read":
            Try(() => Console.WriteLine($"Test server: read OK {File.ReadAllText(argument).Trim()}"));
            break;
        case "write":
            Try(() =>
            {
                File.WriteAllText(argument, "written by the test server");
                Console.WriteLine("Test server: write OK");
            });
            break;
        case "handle":
            // A handle value that was valid in the agent is only valid here if it was inherited.
            var valid = Native.GetHandleInformation(nint.Parse(argument, CultureInfo.InvariantCulture), out _);
            Console.WriteLine($"Test server: handle {argument} {(valid ? "valid" : "invalid")}");
            break;
        case "privileges":
            using (var whoami = Process.Start(new ProcessStartInfo("whoami.exe", "/priv /fo csv /nh") { UseShellExecute = false, RedirectStandardOutput = true }))
            {
                var names = whoami!.StandardOutput.ReadToEnd()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(row => row.Split(',')[0].Trim('"'));
                whoami.WaitForExit();
                Console.WriteLine($"Test server: privileges={string.Join(';', names)}");
            }

            break;
        default:
            Console.WriteLine($"Test server: unknown command '{command}'");
            break;
    }
}

// Ctrl+C also ends the pending read; the handler runs on another thread and may not have run yet.
if (interrupted.Wait(TimeSpan.FromMilliseconds(500)))
{
    return 130;
}

Console.WriteLine("Test server: input closed");
return 0;

static void WriteToConsoleBuffer(string text)
{
    try
    {
        // Straight to the console buffer, bypassing redirected handles, as srcds and many games do.
        using var conout = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
        using var writer = new StreamWriter(conout) { AutoFlush = true };
        writer.WriteLine($"Test server (console): {text}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"Test server: no console ({ex.GetType().Name})");
    }
}

static void Try(Action action)
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"Test server: DENIED {ex.GetType().Name}");
    }
}

internal static partial class Native
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetHandleInformation(nint handle, out uint flags);
}
