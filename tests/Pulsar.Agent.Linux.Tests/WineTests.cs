using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Manifests;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>Games run with Wine (<c>runtime.linux-x64.wine</c>): Wine's loader, the server's own prefix, no display.</summary>
[Collection(RootTests.Name)]
public sealed class WineTests
{
    private const string Root = "/var/lib/pulsar/servers/abcd2345";

    [LinuxFact]
    public void TheGameGetsItsOwnPrefixWhichTheManifestCannotChange()
    {
        var account = new LinuxAccount("pulsar_abcd2345", 990, 990, Root);

        var environment = LinuxGameProcessManager.BuildEnvironment(
            new Dictionary<string, string> { ["WINEPREFIX"] = "/tmp/elsewhere", ["WINEDEBUG"] = "err+all" }, account, Root, null,
            winePrefix: LinuxWine.PrefixOf(Root));

        Assert.Equal($"{Root}/.pulsar/wine", environment[ManifestValidator.WinePrefixVariable]);
        // Wine's quiet defaults are the manifest's to change.
        Assert.Equal("err+all", environment["WINEDEBUG"]);
        Assert.Equal("mscoree,mshtml=", environment["WINEDLLOVERRIDES"]);
        Assert.Equal(Root, environment["HOME"]);
        // Wine 9.0 aborts at its start with TMPDIR set.
        Assert.False(environment.ContainsKey("TMPDIR"));
    }

    [LinuxFact]
    public void ANativeGameGetsNothingOfWines()
    {
        var account = new LinuxAccount("pulsar_abcd2345", 990, 990, Root);

        var environment = LinuxGameProcessManager.BuildEnvironment(new Dictionary<string, string>(), account, Root, null);

        Assert.DoesNotContain(environment.Keys, name => name.StartsWith("WINE", StringComparison.Ordinal));
        Assert.Equal($"{Root}/.pulsar/temp", environment["TMPDIR"]);
    }

    [LinuxFact]
    public void GameExecTakesThePrefix()
    {
        var args = GameExec.Arguments(1000, 1001, "/run/env", null, "/srv", "/usr/lib/wine/wine64", ["/srv/Game.exe", "-x"], winePrefix: "/srv/.pulsar/wine");

        Assert.True(GameExec.TryParse(args, out var request, out _));
        Assert.Equal("/srv/.pulsar/wine", request.WinePrefix);
        Assert.Equal("/usr/lib/wine/wine64", request.Executable);
        Assert.Equal(["/srv/Game.exe", "-x"], request.Arguments);
        Assert.False(GameExec.TryParse(["game-exec", "--uid", "1000", "--gid", "1001", "--env-file", "/run/env", "--wine-prefix", "relative", "--cwd", "/srv", "--", "/bin/true"],
            out _, out _));
        Assert.False(GameExec.TryParse(["game-exec", "--uid", "1000", "--gid", "1001", "--env-file", "/run/env", "--wine-prefix", "/a", "--wine-prefix", "/b", "--cwd", "/srv",
            "--", "/bin/true"], out _, out _));
    }

    [LinuxFact]
    public void ANewPrefixStartsWithTheNullGraphicsDriver()
    {
        var prefix = Path.Combine(LinuxOnly.NewFolder(), ".pulsar", "wine");

        GameExec.PrepareWinePrefix(prefix);

        var registry = File.ReadAllText(Path.Combine(prefix, "user.reg"), Encoding.UTF8);
        Assert.StartsWith("WINE REGISTRY Version 2\n", registry, StringComparison.Ordinal);
        Assert.Contains("[Software\\\\Wine\\\\Drivers]\n\"Graphics\"=\"null\"\n", registry, StringComparison.Ordinal);
        Assert.Equal(0x1C0u, Libc.Lstat(prefix)!.Value.Permissions);
        Assert.Equal(0x180u, Libc.Lstat(Path.Combine(prefix, "user.reg"))!.Value.Permissions);
    }

    [LinuxFact]
    public void APrefixWineAlreadyMadeIsKeptAsItIs()
    {
        var prefix = Path.Combine(LinuxOnly.NewFolder(), "wine");
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "the game's own");

        GameExec.PrepareWinePrefix(prefix);

        Assert.Equal("the game's own", File.ReadAllText(Path.Combine(prefix, "user.reg")));
    }

    [LinuxFact]
    public void OnlyAProgramOfRootsInRootsFoldersIsWines()
    {
        Assert.True(LinuxWine.IsTrusted("/usr/bin/true"));

        // A copy anyone could have put there: in the temporary folder, which every user can write to.
        var copy = Path.Combine(LinuxOnly.NewFolder(), "wine64");
        File.Copy("/usr/bin/true", copy);
        Assert.False(LinuxWine.IsTrusted(copy));
        Assert.False(LinuxWine.IsTrusted("/usr/bin/no-such-wine"));
        Assert.False(LinuxWine.IsTrusted("/etc/passwd")); // not a program
    }

    [LinuxRootFact]
    public void DeletingTheServerDeletesWhatWinesServerLeftForItsUser()
    {
        var servers = Path.Combine(LinuxOnly.NewFolder(), "servers");
        var accounts = new LinuxServerAccounts(NullLogger<LinuxServerAccounts>.Instance);
        var provisioner = new LinuxServerProvisioner(accounts, development: false, NullLogger<LinuxServerProvisioner>.Instance);
        var shortId = "lx" + Guid.NewGuid().ToString("N")[..8];
        provisioner.Provision(servers, shortId);
        var account = accounts.Lookup(LinuxServerAccounts.AccountName(shortId))!;
        var folder = LinuxServerProvisioner.WineServerFolder(account.Uid);
        Directory.CreateDirectory(Path.Combine(folder, "server-801-1234"));

        provisioner.Deprovision(servers, shortId);

        Assert.False(Directory.Exists(folder));
        Assert.Null(accounts.Lookup(LinuxServerAccounts.AccountName(shortId)));
    }
}
