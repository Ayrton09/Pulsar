// Spike 2 — launch a process as a dedicated per-server Windows account from SYSTEM (session 0),
// inside a Job Object with a ConPTY, and check isolation between two "servers".
// Throwaway code: see spikes/README.md. It CREATES LOCAL USERS and CHANGES USER RIGHTS, so run it
// only in a disposable environment (Windows Sandbox or a VM) through run-spike2.ps1.
//
// Usage (as SYSTEM): IsolatedLaunch --probe-dir <ConsoleProbe bin dir> --root <work dir> --report <dir>
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ConsoleCapture;
using IsolatedLaunch;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

string? probeDir = null, root = null, reportDir = null;
for (var i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--probe-dir": probeDir = Path.GetFullPath(args[i + 1]); break;
        case "--root": root = Path.GetFullPath(args[i + 1]); break;
        case "--report": reportDir = Path.GetFullPath(args[i + 1]); break;
    }
}

if (probeDir is null || root is null || reportDir is null)
{
    Console.Error.WriteLine("Usage: IsolatedLaunch --probe-dir <dir> --root <dir> --report <dir>");
    return 2;
}

Directory.CreateDirectory(reportDir);
using var log = new StreamWriter(Path.Combine(reportDir, "spike2.log"), append: false) { AutoFlush = true };
void Log(string message)
{
    var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {message}";
    log.WriteLine(line);
    Console.WriteLine(line);
}

var identity = WindowsIdentity.GetCurrent();
Log($"running as {identity.Name} (IsSystem={identity.IsSystem}), session {System.Diagnostics.Process.GetCurrentProcess().SessionId}, OS {Environment.OSVersion.VersionString}");
if (!identity.IsSystem)
{
    Log("WARNING: not running as SYSTEM; CreateProcessAsUser needs SeAssignPrimaryTokenPrivilege and will likely fail.");
}

// The work directory is deleted at the end, so it must be one this run creates.
if (Directory.Exists(root))
{
    Log($"refusing to run: {root} already exists (pass a new, empty location)");
    return 2;
}

var serversRoot = Path.Combine(root, "servers");
var accounts = new List<ServerAccount>();
try
{
    // 1. Directory layout and ACLs: servers root only for SYSTEM/Administrators, one subfolder per server.
    Directory.CreateDirectory(serversRoot);
    Acl.Restrict(serversRoot);
    Log($"created {serversRoot} with inheritance disabled (SYSTEM + Administrators only)");

    foreach (var name in new[] { "a", "b" })
    {
        var account = ServerAccount.Create($"pulsar_spk_{name}", Log);
        accounts.Add(account);

        var serverDir = Path.Combine(serversRoot, name);
        Directory.CreateDirectory(serverDir);
        Acl.GrantModify(serverDir, account.Sid);
        CopyDirectory(probeDir, Path.Combine(serverDir, "probe"));
        File.WriteAllText(Path.Combine(serverDir, name == "a" ? "own.txt" : "secret.txt"), $"file of server {name}");
        Log($"server {name}: {serverDir} (Modify for {account.Name})");
    }

    var a = accounts[0];
    var probeExe = Path.Combine(serversRoot, "a", "probe", "ConsoleProbe.exe");
    string[] script =
    [
        "whoami",
        "env",
        $"read {Path.Combine(serversRoot, "a", "own.txt")}",
        $"read {Path.Combine(serversRoot, "b", "secret.txt")}",
        $"write {Path.Combine(serversRoot, "b", "injected.txt")}",
        $"write {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "pulsar-spike.txt")}",
        "spawn",
        "quit",
    ];

    // 2. Scenarios: what is the minimum needed for the child to start correctly?
    await RunScenario("s1-token-only", a, probeExe, script, loadProfile: false, desktop: null);
    await RunScenario("s2-profile-and-environment", a, probeExe, script, loadProfile: true, desktop: null);

    using (var desktop = DedicatedDesktop.Create("PulsarSpike", a.Sid, Log))
    {
        await RunScenario("s3-profile-environment-desktop", a, probeExe, script, loadProfile: true, desktop: desktop.Name);
    }

    Log("done; see the s*.jsonl captures next to this log");
    return 0;
}
catch (Exception ex)
{
    Log($"FAILED: {ex}");
    return 1;
}
finally
{
    foreach (var account in accounts)
    {
        account.Delete(Log);
    }

    try
    {
        Directory.Delete(root, recursive: true);
        Log($"removed {root}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Log($"could not remove {root}: {ex.Message}");
    }
}

async Task RunScenario(string name, ServerAccount account, string exe, string[] commands, bool loadProfile, string? desktop)
{
    Log($"--- scenario {name}: loadProfile={loadProfile} desktop={desktop ?? "(inherited)"}");
    using var logon = account.LogOn(loadProfile, Log);
    var actions = commands.Select((c, i) => new ScheduledAction(TimeSpan.FromSeconds(2 + (i * 1.5)), c)).ToList();
    var options = new CaptureOptions(CaptureMode.Pty, exe, Path.GetDirectoryName(exe), null,
        DurationSeconds: 2 + (commands.Length * 2), actions, [], Encoding.UTF8, UseStdHandlesInPty: true, "\r\n")
    {
        UserToken = logon.Token,
        EnvironmentBlock = logon.Environment,
        Desktop = desktop,
    };

    using var recorder = new Recorder(Path.Combine(reportDir!, $"{name}.jsonl"), Encoding.UTF8);
    try
    {
        var summary = await CaptureRunner.RunAsync(options, recorder);
        Log($"{name}: exitCode=0x{summary.ExitCode:X8} firstOutputMs={summary.FirstOutputMs} bytes={summary.Bytes} " +
            $"jobTotalProcesses={summary.JobTotalProcesses} activeAtEnd={summary.JobActiveProcessesAtEnd} killed={summary.KilledByCapture}");
    }
    catch (InvalidOperationException ex)
    {
        Log($"{name}: launch failed: {ex.Message}");
    }
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

namespace IsolatedLaunch
{
    internal static class Acl
    {
        private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

        public static void Restrict(string path)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(FullControl(s_system));
            security.AddAccessRule(FullControl(s_administrators));
            new DirectoryInfo(path).SetAccessControl(security);
        }

        public static void GrantModify(string path, SecurityIdentifier sid)
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }

        private static FileSystemAccessRule FullControl(SecurityIdentifier sid) => new(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
    }
}
