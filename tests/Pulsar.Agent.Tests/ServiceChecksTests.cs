using Pulsar.Agent.Core;
using Pulsar.Agent.Core.Security;

namespace Pulsar.Agent.Tests;

/// <summary>What keeps the agent from running as the SYSTEM service when that would be unsafe.</summary>
public sealed class ServiceChecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-service-checks", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ADevelopmentEnrollmentCannotRunAsTheService()
    {
        // Its servers would run as the current user, which for the service is SYSTEM (ADR-0005).
        var configs = Enrolled(development: true);

        var refusal = ServiceChecks.DevelopmentRefusal(configs.Paths);

        Assert.NotNull(refusal);
        Assert.Contains("development", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AProductionEnrollmentOrNoneCanRunAsTheService()
    {
        Assert.Null(ServiceChecks.DevelopmentRefusal(new AgentPaths(_root))); // not enrolled yet: install first, enroll later
        Assert.Null(ServiceChecks.DevelopmentRefusal(Enrolled(development: false).Paths));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void TheServiceChecksApplyWhenTheAgentRunsAsTheService(bool interactive, bool system, bool asService) =>
        Assert.Equal(asService, ServiceChecks.RunsAsService(interactive, system));

    [WindowsFact]
    public void AnExecutableOthersCanReplaceIsRefusedWithTheReason()
    {
        // The temp folder is the current user's: the check names who could replace the executable.
        var executable = Path.Combine(Directory.CreateDirectory(_root).FullName, "PulsarAgent.exe");
        File.WriteAllBytes(executable, [0]);

        Assert.NotNull(ServiceChecks.ExecutableRefusal(executable));
        Assert.Null(ServiceChecks.ExecutableRefusal(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
    }

    private AgentConfigStore Enrolled(bool development)
    {
        var configs = new AgentConfigStore(new AgentPaths(_root));
        configs.Save(new AgentConfig(new Uri("https://panel.example.com/"), Guid.NewGuid(), Guid.NewGuid(),
            new NodeKeyReference("PulsarAgent-1", NodeKeyScope.Machine, NodeKeyStorage.Software), [], null, DateTimeOffset.UnixEpoch, development));
        return configs;
    }
}
