// Test target for spike 1: mimics the console behaviors of real game servers so ConsoleCapture can be
// validated before running it against cs2.exe / srcds.exe / steamcmd.exe.
//   - writes to stdout, to stderr and directly to the console buffer (CONOUT$), like srcds does;
//   - emits ANSI colors;
//   - echoes input lines, exits on "quit", reports Ctrl+C;
//   - "spawn" starts a grandchild that sleeps, to check that killing the job leaves no orphans.
using System.Diagnostics;

if (args is ["--sleep"])
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

Console.CancelKeyPress += (_, e) =>
{
    Console.WriteLine("probe: ctrl-c received, exiting with 130");
    Environment.Exit(130);
};

Console.WriteLine("probe: hello from stdout");
Console.Error.WriteLine("probe: hello from stderr");
Console.WriteLine("probe: \u001b[32mgreen ANSI text\u001b[0m and unicode ñ á ü €");

try
{
    // Write straight to the console buffer, bypassing redirected standard handles.
    using var conout = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
    using var writer = new StreamWriter(conout) { AutoFlush = true };
    writer.WriteLine("probe: hello from CONOUT$ (console API)");
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
{
    Console.WriteLine($"probe: no console attached ({ex.GetType().Name})");
}

Console.WriteLine("probe: ready, type a command");

string? line;
while ((line = Console.ReadLine()) is not null)
{
    switch (line.Trim())
    {
        case "quit":
            Console.WriteLine("probe: bye");
            return 0;
        case "spawn":
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--sleep") { UseShellExecute = false });
            Console.WriteLine($"probe: spawned grandchild pid {child?.Id}");
            break;
        case "crash":
            throw new InvalidOperationException("probe: crash requested");
        case "env":
            foreach (var name in new[] { "USERNAME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP" })
            {
                Console.WriteLine($"probe: {name}={Environment.GetEnvironmentVariable(name)}");
            }
            break;
        case "whoami":
            // Full token: user, groups (including nested BUILTIN\Users) and privileges.
            using (var whoami = Process.Start(new ProcessStartInfo("whoami.exe", "/all") { UseShellExecute = false, RedirectStandardOutput = true }))
            {
                Console.WriteLine(whoami?.StandardOutput.ReadToEnd());
                whoami?.WaitForExit();
            }
            break;
        case var read when read.StartsWith("read ", StringComparison.Ordinal):
            Try(() => Console.WriteLine($"probe: read OK: {File.ReadAllText(read[5..].Trim()).Trim()}"));
            break;
        case var write when write.StartsWith("write ", StringComparison.Ordinal):
            Try(() =>
            {
                File.WriteAllText(write[6..].Trim(), "written by probe");
                Console.WriteLine("probe: write OK");
            });
            break;
        default:
            Console.WriteLine($"probe: got '{line}'");
            break;
    }
}

Console.WriteLine("probe: stdin closed");
return 0;

static void Try(Action action)
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"probe: DENIED {ex.GetType().Name}: {ex.Message}");
    }
}
