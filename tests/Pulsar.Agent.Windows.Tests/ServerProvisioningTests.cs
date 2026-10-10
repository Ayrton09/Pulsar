using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Processes;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// Creates real local accounts and changes user rights, so it only runs where that is acceptable: an elevated, disposable
/// machine that opts in with <c>PULSAR_ACCOUNT_TESTS=1</c> (the Windows CI runner, Windows Sandbox). Never on a
/// developer's machine by accident.
/// </summary>
public sealed class AccountFactAttribute : FactAttribute
{
    public AccountFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Creates local accounts: needs an elevated, disposable machine with PULSAR_ACCOUNT_TESTS=1.";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(AccountFactAttribute);
    }

    public static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PULSAR_ACCOUNT_TESTS") == "1"
        && WindowsDirectorySecurity.IsElevated();

    /// <summary>Starting a process as another account (CreateProcessAsUser) needs SYSTEM, as the agent service runs.</summary>
    public static bool IsSystem
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
    }
}

public sealed class ServerProvisioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-provisioning-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _shortIds = [];

    public void Dispose()
    {
        if (AccountFactAttribute.Enabled)
        {
            var provisioner = Provisioner(development: false);
            foreach (var shortId in _shortIds)
            {
                provisioner.Deprovision(_root, shortId);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [WindowsFact]
    public void ADevelopmentServerDirectoryIsPrivateToTheAgentUser()
    {
        var provisioner = Provisioner(development: true);

        var server = provisioner.Provision(_root, "dev1test");
        var again = provisioner.Provision(_root, "dev1test");

        Assert.Equal(server, again);
        Assert.Equal(ProcessIdentity.CurrentUser, server.Identity);
        Assert.Equal(Path.Combine(_root, "dev1test"), server.Directory);
        var rules = Rules(server.Directory);
        Assert.True(new DirectoryInfo(server.Directory).GetAccessControl().AreAccessRulesProtected);
        Assert.Contains(WindowsIdentity.GetCurrent().User!, rules.Keys);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), rules.Keys);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), rules.Keys);
    }

    [WindowsFact]
    public void DeletingAServerNeverFollowsAJunctionOutOfItsDirectory()
    {
        var provisioner = Provisioner(development: true);
        var server = provisioner.Provision(_root, "dev2test");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
        File.WriteAllText(Path.Combine(server.Directory, "game.txt"), "server data");
        CreateJunction(Path.Combine(server.Directory, "escape"), outside);

        provisioner.Deprovision(_root, "dev2test");
        provisioner.Deprovision(_root, "dev2test");

        Assert.False(Directory.Exists(server.Directory));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [WindowsFact]
    public void AFolderSwappedForAJunctionWhileDeletingIsNotFollowed()
    {
        // The server's account swaps a folder the agent has just checked for a junction to somewhere else.
        var server = Directory.CreateDirectory(Path.Combine(_root, "dev3test")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
        Directory.CreateDirectory(Path.Combine(server, "game"));
        File.WriteAllText(Path.Combine(server, "game", "map.bsp"), "server data");
        var swapped = false;

        try
        {
            WindowsServerProvisioner.DeleteTree(server, beforeListing: directory =>
            {
                if (!swapped && directory.EndsWith("game", StringComparison.Ordinal))
                {
                    swapped = true;
                    Directory.Move(Path.Combine(server, "game"), Path.Combine(server, "moved"));
                    CreateJunction(Path.Combine(server, "game"), outside);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Files.FileManagerException)
        {
            // Refusing is fine; deleting outside is not.
            TestContext.Current.SendDiagnosticMessage($"Refused: {ex.Message}");
        }

        if (Directory.Exists(Path.Combine(server, "game")))
        {
            Directory.Delete(Path.Combine(server, "game")); // the junction itself, before the test folder goes
        }

        Assert.True(swapped);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [WindowsFact]
    public void NamesWindowsPathsWouldChangeAreDeletedAndNothingOutside()
    {
        // A server's account can make "name." and "name " with \\?\ paths; as a path, Windows reads them as "name".
        var server = Directory.CreateDirectory(Path.Combine(_root, "dev5test")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "dev5test-outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");
        Directory.CreateDirectory(Path.Combine(server, "name"));
        File.WriteAllText(Path.Combine(server, "name", "keep.txt"), "server data");
        Directory.CreateDirectory(@"\\?\" + Path.Combine(server, "name."));
        File.WriteAllText(@"\\?\" + Path.Combine(server, "name.", "map. "), "server data");
        File.WriteAllText(@"\\?\" + Path.Combine(server, "name "), "server data");
        Directory.CreateDirectory(@"\\?\" + Path.Combine(server, "dots.."));

        WindowsServerProvisioner.DeleteTree(server);

        Assert.False(Directory.Exists(server));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [WindowsFact]
    public void ADeepTreeIsDeleted()
    {
        var server = Directory.CreateDirectory(Path.Combine(_root, "dev6test")).FullName;
        var deepest = @"\\?\" + server + string.Concat(Enumerable.Repeat(@"\d", 600));
        Directory.CreateDirectory(deepest);
        File.WriteAllText(Path.Combine(deepest, "bottom.txt"), "server data");

        WindowsServerProvisioner.DeleteTree(server);

        Assert.False(Directory.Exists(server));
    }

    [WindowsFact]
    public void ShortIdsThatCouldEscapeOrBreakAnAccountNameAreRefused()
    {
        var provisioner = Provisioner(development: true);

        foreach (var shortId in new[] { "..", "a/b", "ABCD", "abc", "abcdefghijklm", "abcd ", "" })
        {
            Assert.Throws<ArgumentException>(() => provisioner.Provision(_root, shortId));
        }
    }

    [AccountFact]
    public void AProductionServerGetsItsOwnAccountWithAccessToItsDirectoryOnly()
    {
        var provisioner = Provisioner(development: false);
        var shortId = NewShortId();

        var server = provisioner.Provision(_root, shortId);

        var name = WindowsServerAccounts.AccountName(shortId);
        Assert.Equal(ProcessIdentity.Account(name), server.Identity);
        Assert.True(WindowsServerAccounts.Exists(name));
        var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier));
        var rules = Rules(server.Directory);
        Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, rules[sid]);
        Assert.Equal(FileSystemRights.Modify | FileSystemRights.Synchronize, rules[new SecurityIdentifier("S-1-3-4")]);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), rules.Keys);
        Assert.DoesNotContain(sid, Rules(_root).Keys);

        provisioner.Deprovision(_root, shortId);

        Assert.False(WindowsServerAccounts.Exists(name));
        Assert.False(Directory.Exists(server.Directory));
    }

    [AccountFact]
    public void TheAccountLogsOnWithItsOwnProfileAndNoPrivilegesButChangeNotify()
    {
        var accounts = TestAccounts.Create();
        var shortId = NewShortId();
        Provisioner(development: false).Provision(_root, shortId);

        using var logon = accounts.LogOn(WindowsServerAccounts.AccountName(shortId));

        Assert.EndsWith(WindowsServerAccounts.AccountName(shortId), logon.Environment["USERPROFILE"], StringComparison.OrdinalIgnoreCase);
        using var identity = new WindowsIdentity(logon.TokenHandle);
        Assert.Equal(logon.Sid, identity.User);
    }

    [AccountFact]
    public async Task AServerRunsAsItsAccountAndCannotReadAnotherServer()
    {
        Assert.SkipUnless(AccountFactAttribute.IsSystem, "CreateProcessAsUser needs SYSTEM (the agent service's account).");
        var provisioner = Provisioner(development: false);
        string[] shortIds = [NewShortId(), NewShortId()];
        var servers = shortIds.Select(id => provisioner.Provision(_root, id)).ToList();
        File.WriteAllText(Path.Combine(servers[1].Directory, "secret.txt"), "server b");
        using var game = new TestServerDirectory();
        CopyDirectory(Path.Combine(game.Root, "bin"), Path.Combine(servers[0].Directory, "bin"));

        var spec = new ProcessLaunchSpec(Guid.NewGuid(), servers[0].Directory, TestServerDirectory.Executable, ["--ready-after", "100"], "",
            new Dictionary<string, string>(), ConsoleMode.Pty, ConsoleEncoding.Utf8, servers[0].Identity);
        await using var process = await TestServerDirectory.Manager().StartAsync(spec, TestContext.Current.CancellationToken);
        var output = new OutputCollector(process);
        await output.WaitForAsync("Test server ready");

        await process.WriteInputAsync("whoami\r\n", TestContext.Current.CancellationToken);
        var identity = await output.WaitForAsync("user=");
        Assert.Contains($"\\{WindowsServerAccounts.AccountName(shortIds[0])}\r", identity, StringComparison.OrdinalIgnoreCase);
        await process.WriteInputAsync($"read {Path.Combine(servers[1].Directory, "secret.txt")}\r\n", TestContext.Current.CancellationToken);
        await output.WaitForAsync("Test server: DENIED");
        await process.WriteInputAsync("privileges\r\n", TestContext.Current.CancellationToken);
        var text = await output.WaitForAsync("privileges=");
        Assert.Contains("privileges=SeChangeNotifyPrivilege\r", text, StringComparison.Ordinal);
        await process.WriteInputAsync("quit\r\n", TestContext.Current.CancellationToken);
        Assert.Equal(0, await process.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    private static WindowsServerProvisioner Provisioner(bool development) => new(TestAccounts.Create(),
        development, NullLogger<WindowsServerProvisioner>.Instance);

    private string NewShortId()
    {
        var shortId = "t" + Guid.NewGuid().ToString("N")[..9];
        _shortIds.Add(shortId);
        return shortId;
    }

    private static Dictionary<SecurityIdentifier, FileSystemRights> Rules(string directory) =>
        new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .GroupBy(r => (SecurityIdentifier)r.IdentityReference)
            .ToDictionary(g => g.Key, g => g.Aggregate((FileSystemRights)0, (all, r) => all | r.FileSystemRights));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }
}
