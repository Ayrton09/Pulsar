using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Processes;
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
    public void TheNodeKeyAndAPendingOneAreDeletedAndOneThatCannotBeIsReported()
    {
        var current = new NodeKeyReference("PulsarAgent-current", NodeKeyScope.Machine, NodeKeyStorage.Software);
        var pending = new NodeKeyReference("PulsarAgent-pending", NodeKeyScope.Machine, NodeKeyStorage.Software);
        var config = Config(current, pending);
        List<string> deleted = [];
        using var errors = new StringWriter();

        // The current key cannot be deleted (a key store that refuses): the pending one still goes, and the failure is reported.
        var all = AgentCommands.DeleteNodeKeys(config, key =>
        {
            if (key == current)
            {
                throw new System.Security.Cryptography.CryptographicException("Access is denied.");
            }

            deleted.Add(key.Name);
        }, errors);

        Assert.False(all);
        Assert.Equal(["PulsarAgent-pending"], deleted);
        Assert.Contains("PulsarAgent-current", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("by hand", errors.ToString(), StringComparison.Ordinal);
        // Unenroll is local: the credential stays valid in the panel until the node is deleted there.
        Assert.DoesNotContain("no longer accepts", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatCannotBeDeletedKeepsTheConfigurationAndFailsSoARetryCanDeleteIt()
    {
        var current = new NodeKeyReference("PulsarAgent-current", NodeKeyScope.Machine, NodeKeyStorage.Software);
        var config = Config(current, null);
        new AgentConfigStore(_paths).Save(config);
        var refuse = true;
        List<string> deleted = [];
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Action<NodeKeyReference> delete = key =>
        {
            if (refuse)
            {
                throw new System.Security.Cryptography.CryptographicException("Access is denied.");
            }

            deleted.Add(key.Name);
        };

        var first = AgentCommands.FinishUnenrollment(_paths, config, delete, service: true, output, errors);

        Assert.Equal(1, first);
        Assert.True(File.Exists(_paths.ConfigFile)); // the retry finds the key it names
        Assert.DoesNotContain("are deleted", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("again", errors.ToString(), StringComparison.Ordinal);

        refuse = false;
        var retry = AgentCommands.FinishUnenrollment(_paths, new AgentConfigStore(_paths).Load(), delete, service: true, output, errors);

        Assert.Equal(0, retry);
        Assert.Equal(["PulsarAgent-current"], deleted);
        Assert.False(File.Exists(_paths.ConfigFile));
        Assert.Contains("the node key and the configuration are deleted and the service is removed", output.ToString(), StringComparison.Ordinal);
    }

    private static AgentConfig Config(NodeKeyReference key, NodeKeyReference? pending) =>
        new(new Uri("https://panel.example.com/"), Guid.NewGuid(), Guid.NewGuid(), key, [], null, DateTimeOffset.UtcNow, Development: false,
            PendingRotation: pending is null ? null : new PendingRotation(pending, Guid.NewGuid(), DateTimeOffset.UtcNow));

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
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, Path.Combine(_root, "backups"), new PlainStateProtector(), name =>
        {
            deleted.Add(name);
            return true;
        }, () => 3, output, errors);

        Assert.Equal(["pulsar_abcd2345", "pulsar_efgh6789"], deleted);
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
            () => throw new UnauthorizedAccessException("Access is denied."), output, errors);

        Assert.False(Directory.Exists(_paths.SteamLoginsDirectory));
        Assert.Contains("firewall", errors.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheServersHttpUrlsAreReleasedBeforeTheStateThatRecordsThemGoes()
    {
        new SteamLoginStore(_paths, new PlainStateProtector()).Save(new SteamLogin(Guid.NewGuid(), "builder", "password"), "cached"u8.ToArray());
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var stateThere = false;

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), deleteAccount: null, () => 0, output, errors,
            removeHttpUrls: () =>
            {
                stateThere = Directory.Exists(_paths.SteamLoginsDirectory);
                return 2;
            });

        Assert.True(stateThere);
        Assert.Contains("Released 2 HTTP URL reservation(s)", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_paths.SteamLoginsDirectory));
    }

    [Fact]
    public void HttpUrlsThatCannotBeReleasedAreReportedAndTheRestGoesOn()
    {
        new SteamLoginStore(_paths, new PlainStateProtector()).Save(new SteamLogin(Guid.NewGuid(), "builder", "password"), "cached"u8.ToArray());
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), deleteAccount: null, () => 0, output, errors,
            removeHttpUrls: () => throw new UnauthorizedAccessException("Access is denied."));

        Assert.Contains("netsh http show urlacl", errors.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_paths.SteamLoginsDirectory));
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
        }, () => 1, output, errors, serviceStillStopping: true);

        Assert.Empty(deleted);
        Assert.True(Directory.Exists(_paths.ServersStateDirectory));
        Assert.True(Directory.Exists(_paths.SteamLoginsDirectory));
        Assert.Contains("1 firewall rule(s)", output.ToString(), StringComparison.Ordinal);
        var printed = errors.ToString();
        Assert.Contains("pulsar_abcd2345", printed, StringComparison.Ordinal);
        Assert.Contains(Path.GetDirectoryName(_paths.ServersStateDirectory)!, printed, StringComparison.Ordinal);
        Assert.Contains("by hand", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatStillRunsAsAServerAccountIsEndedBeforeItIsDeletedAndAnAccountWithProcessesLeftStays()
    {
        Directory.CreateDirectory(Path.Combine(_servers, "abcd2345"));
        Directory.CreateDirectory(Path.Combine(_servers, "efgh6789"));
        List<string> calls = [];
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), name =>
        {
            calls.Add($"delete {name}");
            return true;
        }, () => 0, output, errors, endProcesses: name =>
        {
            calls.Add($"end {name}");
            return name == "pulsar_abcd2345" ? 2 : 0; // two processes of the first account cannot be ended
        });

        Assert.Equal(["end pulsar_abcd2345", "end pulsar_efgh6789", "delete pulsar_efgh6789"], calls);
        Assert.Contains("1 server account(s)", output.ToString(), StringComparison.Ordinal);
        var printed = errors.ToString();
        Assert.Contains("pulsar_abcd2345", printed, StringComparison.Ordinal);
        Assert.Contains("2 process(es)", printed, StringComparison.Ordinal);
        Assert.Contains("Computer Management", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAccountWhoseProcessesCannotBeListedIsKeptAndTheRestGoesOn()
    {
        Directory.CreateDirectory(Path.Combine(_servers, "abcd2345"));
        List<string> deleted = [];
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), name =>
        {
            deleted.Add(name);
            return true;
        }, () => 0, output, errors, endProcesses: _ => throw new System.ComponentModel.Win32Exception(5, "Access is denied."));

        Assert.Empty(deleted);
        Assert.Contains("pulsar_abcd2345", errors.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_paths.ServersStateDirectory));
    }

    [Fact]
    public void EveryFirewallRuleOfTheAgentGoesWhateverServerItWasForAndOnlyThose()
    {
        // The count comes from whatever the delegate removed: no short id of a record or a folder limits it.
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var asked = 0;

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), deleteAccount: null, () =>
        {
            asked++;
            return 4;
        }, output, errors);

        Assert.Equal(1, asked);
        Assert.Contains("4 firewall rule(s)", output.ToString(), StringComparison.Ordinal);
        Assert.True(WindowsServerFirewall.IsAgentRule("Pulsar abcd2345 game udp"));
        Assert.True(WindowsServerFirewall.IsAgentRule("Pulsar zzzz9999 panel block")); // a server with no record and no folder
        Assert.False(WindowsServerFirewall.IsAgentRule("Pulsar"));
        Assert.False(WindowsServerFirewall.IsAgentRule("PulsarX abcd2345 game udp"));
        Assert.False(WindowsServerFirewall.IsAgentRule("pulsar abcd2345 game udp"));
        Assert.False(WindowsServerFirewall.IsAgentRule("Core Networking - DNS"));
    }

    [Fact]
    public void ASteamLoginLentByAnAgentThatNeverRanAgainIsDeletedFromEveryServerFolder()
    {
        var lent = Path.Combine(_servers, "abcd2345", ".pulsar", "steamcmd", "config", "config.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(lent)!);
        File.WriteAllText(lent, "a cached login");
        var other = Path.Combine(_servers, "efgh6789", ".pulsar", "steamcmd", "config", "config.vdf"); // a folder with no record
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "another");
        File.WriteAllText(Path.Combine(_servers, "abcd2345", ".pulsar", "steamcmd", "steamcmd.exe"), "kept"); // the server's SteamCMD stays
        Directory.CreateDirectory(Path.Combine(_servers, "ijkl2345")); // none lent
        using var output = new StringWriter();
        using var errors = new StringWriter();

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), deleteAccount: null, () => 0, output, errors,
            files: new DirectServerFiles());

        Assert.False(File.Exists(lent));
        Assert.False(File.Exists(other));
        Assert.False(Directory.Exists(Path.GetDirectoryName(lent)!)); // the empty folder goes too
        Assert.True(File.Exists(Path.Combine(_servers, "abcd2345", ".pulsar", "steamcmd", "steamcmd.exe")));
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void ASteamLoginThatCannotBeDeletedIsReportedAndTheRestGoesOn()
    {
        Directory.CreateDirectory(Path.Combine(_servers, "abcd2345"));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        new SteamLoginStore(_paths, new PlainStateProtector()).Save(new SteamLogin(Guid.NewGuid(), "builder", "password"), "cached"u8.ToArray());

        AgentCommands.RemoveServers(_paths, _servers, _paths.BackupsDirectory, new PlainStateProtector(), deleteAccount: null, () => 0, output, errors,
            files: new RefusingFiles());

        Assert.Contains("abcd2345", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("by hand", errors.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_paths.SteamLoginsDirectory));
    }

    private sealed class RefusingFiles : IServerFiles
    {
        public Stream? OpenRead(string serverRoot, string relativePath) => throw new UnauthorizedAccessException("Access is denied.");

        public void Write(string serverRoot, string relativePath, Stream content) => throw new UnauthorizedAccessException("Access is denied.");

        public void Delete(string serverRoot, string relativePath) => throw new UnauthorizedAccessException("Access is denied.");

        public void CreateDirectory(string serverRoot, string relativePath) => throw new UnauthorizedAccessException("Access is denied.");

        public bool DeleteEmptyDirectory(string serverRoot, string relativePath) => throw new UnauthorizedAccessException("Access is denied.");

        public IReadOnlyList<(string Name, DateTimeOffset LastWrite)> ListFiles(string serverRoot, string relativeDirectory, int limit) =>
            throw new UnauthorizedAccessException("Access is denied.");
    }

    [WindowsFact]
    public void TheAgentsFirewallRulesCanBeFoundWithoutChangingAnything()
    {
        // Reading the rules needs no elevation; a name nobody uses matches nothing, so nothing is removed.
        Assert.Equal(0, WindowsServerFirewall.RemoveWhere(name => name == $"Pulsar {Guid.NewGuid():N}"));
    }
}
