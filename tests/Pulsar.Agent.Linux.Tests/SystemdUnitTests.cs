namespace Pulsar.Agent.Linux.Tests;

/// <summary>The agent's systemd unit, as systemd reads it.</summary>
public sealed class SystemdUnitTests
{
    [LinuxFact]
    public void TheUnitRunsTheAgentAsWrittenWhateverItsPath()
    {
        var unit = SystemdUnits.AgentUnitFile("/opt/pulsar 100%/agent/PulsarAgent", ["--data-dir", "/srv/my \"state\""], TimeSpan.FromSeconds(60));

        var exec = SystemdUnits.ExecStartOf(unit)!;
        Assert.Equal(["/opt/pulsar 100%/agent/PulsarAgent", "run", "--data-dir", "/srv/my \"state\""], SystemdUnits.SplitExecLine(exec));
        // WorkingDirectory= takes the rest of its line: unquoted, its specifiers escaped.
        Assert.Contains("\nWorkingDirectory=/opt/pulsar 100%%/agent\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nTimeoutStopSec=90\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nRuntimeDirectoryPreserve=yes\n", unit, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void TheUnitIsOneSystemdAccepts()
    {
        var unit = Path.Combine(LinuxOnly.NewFolder(), "pulsar-agent.service");
        File.WriteAllText(unit, SystemdUnits.AgentUnitFile("/usr/bin/true", [], TimeSpan.FromSeconds(60)));

        // systemd-analyze says what systemd itself would refuse in it.
        var verdict = SystemdUnits.Run("systemd-analyze", ["verify", unit]);
        Assert.DoesNotContain("not absolute", verdict, StringComparison.Ordinal);
    }

    [LinuxFact]
    public void AControlCharacterIsNeverWrittenIntoAUnit()
    {
        Assert.Throws<ArgumentException>(() => SystemdUnits.AgentUnitFile("/opt/agent/PulsarAgent", ["--data-dir", "/srv/a\nExecStartPre=/bin/sh"], TimeSpan.FromSeconds(60)));
        Assert.Throws<ArgumentException>(() => SystemdUnits.AgentUnitFile("/opt/a\ngent/PulsarAgent", [], TimeSpan.FromSeconds(60)));
    }

    [LinuxTheory]
    [InlineData("not-found", "inactive", UnitState.NotInstalled)]
    [InlineData("loaded", "active", UnitState.Running)]
    [InlineData("loaded", "activating", UnitState.StartPending)]
    [InlineData("loaded", "deactivating", UnitState.StopPending)]
    [InlineData("loaded", "failed", UnitState.Stopped)]
    public void ItsStatesAreTheAgents(string load, string active, UnitState state) => Assert.Equal(state, SystemdUnits.StateOf(load, active));
}
