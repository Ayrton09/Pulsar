using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Security;
using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Core.Steam;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Contracts;

namespace Pulsar.Agent.Tests;

/// <summary>What unenroll removes of the game servers, and what it keeps (no real account or firewall rule is touched).</summary>
public sealed class UnenrollTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-unenroll-tests", Guid.NewGuid().ToString("N"));
    private readonly AgentPaths _paths;
    private readonly string _servers;

    public UnenrollTests()
    {
        _paths = new AgentPaths(Path.Combine(_root, "state"));
        _servers = Directory.CreateDirectory(Path.Combine(_root, "servers")).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheServersSecretsAccountsAndFirewallRulesGoTheirFoldersBackupsAndLogsStay()
    {
        var store = new ServerStore(_paths, new PlainStateProtector());
        store.Save(new ServerRecord(Guid.NewGuid(), new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?> { ["rcon"] = "secret" }, [],
            AutoStart: true, RestartOnCrash: true), DesiredRunning: true, ResumeOnStart: false, RconPassword: "rcon-secret"));
        new SteamLoginStore(_paths, new PlainStateProtector()).Save(new SteamLogin(Guid.NewGuid(), "builder", "password"), "cached"u8.ToArray());
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_servers, "abcd2345")).FullName, "server.cfg"), "hostname a");
        Directory.CreateDirectory(Path.Combine(_servers, "efgh6789")); // its record is gone: its folder still names it
        Directory.CreateDirectory(Path.Combine(_servers, "not a server"));
        Directory.CreateDirectory(_paths.ConsoleLogsDirectory);
        Directory.CreateDirectory(_paths.LogsDirectory);
        List<string> deleted = [];
        IReadOnlyCollection<string>? rulesOf = null;
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, Path.Combine(_root, "backups"), new PlainStateProtector(), name =>
        {
            deleted.Add(name);
            return true;
        }, shortIds =>
        {
            rulesOf = shortIds;
            return 3;
        }, output, errors);

        Assert.Equal(["pulsar_abcd2345", "pulsar_efgh6789"], deleted);
        Assert.Equal(["abcd2345", "efgh6789"], rulesOf);
        Assert.False(Directory.Exists(_paths.ServersStateDirectory));
        Assert.False(Directory.Exists(_paths.SteamLoginsDirectory));
        Assert.Equal("hostname a", File.ReadAllText(Path.Combine(_servers, "abcd2345", "server.cfg")));
        Assert.True(Directory.Exists(_paths.ConsoleLogsDirectory));
        Assert.True(Directory.Exists(_paths.LogsDirectory));
        var printed = output.ToString();
        Assert.Contains("2 server account(s)", printed, StringComparison.Ordinal);
        Assert.Contains("3 firewall rule(s)", printed, StringComparison.Ordinal);
        Assert.Contains(_servers, printed, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_root, "backups"), printed, StringComparison.Ordinal);
        Assert.Contains(_paths.LogsDirectory, printed, StringComparison.Ordinal);
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void ADevelopmentAgentHasNoAccountsToRemoveAndAFirewallItCannotChangeIsReported()
    {
        new SteamLoginStore(_paths, new PlainStateProtector()).Save(new SteamLogin(Guid.NewGuid(), "builder", "password"), "cached"u8.ToArray());
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), deleteAccount: null,
            _ => throw new UnauthorizedAccessException("Access is denied."), output, errors);

        Assert.False(Directory.Exists(_paths.SteamLoginsDirectory));
        Assert.Contains("firewall", errors.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AServiceStillStoppingKeepsTheAccountsAndTheStateItMayStillUse()
    {
        // The service could still save a server record (secrets included) or log on as a server account meanwhile.
        var store = new ServerStore(_paths, new PlainStateProtector());
        store.Save(new ServerRecord(Guid.NewGuid(), new ServerRuntimeSpec("abcd2345", "{}", new Dictionary<string, string?>(), [],
            AutoStart: true, RestartOnCrash: true), DesiredRunning: true, ResumeOnStart: false, RconPassword: "rcon-secret"));
        new SteamLoginStore(_paths, new PlainStateProtector()).Save(new SteamLogin(Guid.NewGuid(), "builder", "password"), "cached"u8.ToArray());
        List<string> deleted = [];
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), name =>
        {
            deleted.Add(name);
            return true;
        }, _ => 1, output, errors, serviceStillStopping: true);

        Assert.Empty(deleted);
        Assert.True(Directory.Exists(_paths.ServersStateDirectory));
        Assert.True(Directory.Exists(_paths.SteamLoginsDirectory));
        Assert.Contains("1 firewall rule(s)", output.ToString(), StringComparison.Ordinal);
        var printed = errors.ToString();
        Assert.Contains("pulsar_abcd2345", printed, StringComparison.Ordinal);
        Assert.Contains(Path.GetDirectoryName(_paths.ServersStateDirectory)!, printed, StringComparison.Ordinal);
        Assert.Contains("by hand", printed, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void TheAgentsFirewallRulesCanBeFoundWithoutChangingAnything()
    {
        // Reading the rules needs no elevation; a name nobody uses matches nothing, so nothing is removed.
        Assert.Equal(0, WindowsServerFirewall.RemoveWhere(name => name == $"Pulsar {Guid.NewGuid():N}"));
    }
}
