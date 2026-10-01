namespace Pulsar.Architecture.Tests;

/// <summary>
/// Enforces the project dependency rules from docs/architecture/01. The rules are checked on the
/// project files rather than on compiled assemblies, because an unused reference does not show up in
/// assembly metadata but still breaks the intended boundaries.
/// </summary>
public sealed class ProjectDependencyTests
{
    private static readonly Dictionary<string, string[]> s_allowedProjectReferences = new()
    {
        ["Pulsar.Domain"] = [],
        ["Pulsar.Manifests"] = [],
        ["Pulsar.Contracts"] = [],
        ["Pulsar.Application"] = ["Pulsar.Domain", "Pulsar.Manifests", "Pulsar.Contracts"],
        ["Pulsar.Infrastructure"] = ["Pulsar.Application", "Pulsar.Domain"],
        ["Pulsar.Api"] = ["Pulsar.Application", "Pulsar.Infrastructure", "Pulsar.Contracts", "Pulsar.Manifests"],
        ["Pulsar.Agent.Core"] = ["Pulsar.Contracts", "Pulsar.Manifests"],
        ["Pulsar.Agent.Windows"] = ["Pulsar.Agent.Core"],
        ["Pulsar.Agent"] = ["Pulsar.Agent.Core", "Pulsar.Agent.Windows"],
    };

    // Shared and core projects must stay free of infrastructure and web frameworks.
    private static readonly string[] s_packageFreeProjects = ["Pulsar.Domain", "Pulsar.Manifests", "Pulsar.Contracts"];

    private static readonly string[] s_forbiddenPackagePrefixesInCore =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Microsoft.AspNetCore",
    ];

    public static TheoryData<string> SourceProjects => new(SourceProjectFiles.All.Keys);

    [Fact]
    public void EverySourceProjectHasDependencyRules()
    {
        var withoutRules = SourceProjectFiles.All.Keys.Where(name => !s_allowedProjectReferences.ContainsKey(name)).ToList();

        Assert.True(withoutRules.Count == 0,
            $"Add dependency rules to {nameof(ProjectDependencyTests)} for: {string.Join(", ", withoutRules)}");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void ProjectOnlyReferencesAllowedProjects(string project)
    {
        Assert.True(s_allowedProjectReferences.TryGetValue(project, out var allowed), $"No rules defined for {project}.");

        var actual = SourceProjectFiles.All[project].ProjectReferences;
        var disallowed = actual.Except(allowed, StringComparer.Ordinal).ToList();

        Assert.True(disallowed.Count == 0,
            $"{project} must not reference {string.Join(", ", disallowed)} (see docs/architecture/01).");
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void AgentProjectsNeverReferencePanelProjects(string project)
    {
        if (!project.StartsWith("Pulsar.Agent", StringComparison.Ordinal))
        {
            return;
        }

        string[] panelProjects = ["Pulsar.Domain", "Pulsar.Application", "Pulsar.Infrastructure", "Pulsar.Api"];
        var references = SourceProjectFiles.All[project].ProjectReferences;

        Assert.Empty(references.Intersect(panelProjects, StringComparer.Ordinal));
    }

    [Fact]
    public void CoreProjectsDoNotDependOnInfrastructureOrWebPackages()
    {
        foreach (var project in s_packageFreeProjects)
        {
            var packages = SourceProjectFiles.All[project].PackageReferences;
            var forbidden = packages
                .Where(p => s_forbiddenPackagePrefixesInCore.Any(prefix => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Assert.True(forbidden.Count == 0, $"{project} must not reference {string.Join(", ", forbidden)}.");
        }
    }
}
