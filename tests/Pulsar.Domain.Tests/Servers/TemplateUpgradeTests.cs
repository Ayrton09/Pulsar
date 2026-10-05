using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Servers;

public sealed class TemplateUpgradeTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    [InlineData("1.2.3-beta.1", "1.2.3", -1)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.10", 1)] // ordinal pre-release order, as documented
    [InlineData("1.2.3+build.5", "1.2.3", 0)]
    public void VersionsAreOrderedByTheirNumbersThenPreReleases(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(GameTemplate.CompareVersions(left, right)));

    [Fact]
    public void AServerMovesOnlyToANewerVersionOfItsOwnGame()
    {
        var current = Template("css", "1.0.0");
        var server = Server(current);
        server.SetVariable("map", "cs_office", null, s_now);
        server.SetVariable("removed", "x", null, s_now);

        Assert.Equal(ServerErrors.TemplateNotNewer, server.ChangeTemplate(current, Template("css", "1.0.0"), ["map"], s_now).Error);
        Assert.Equal(ServerErrors.TemplateNotNewer, server.ChangeTemplate(current, Template("cs2", "2.0.0"), ["map"], s_now).Error);

        var next = Template("css", "1.1.0");
        Assert.True(server.ChangeTemplate(current, next, ["map", "contact"], s_now).IsSuccess);
        Assert.Equal(next.Id, server.TemplateId);
        Assert.Equal(["map"], server.Variables.Select(v => v.Key));
        Assert.Equal(ServerErrors.TemplateNotNewer, server.ChangeTemplate(current, Template("css", "1.2.0"), ["map"], s_now).Error); // not its template any more
    }

    [Fact]
    public void ARunningServerDoesNotChangeItsTemplate()
    {
        var current = Template("css", "1.0.0");
        var server = Server(current);
        server.Observe(ServerRuntimeState.Running, ServerStateReason.Ready, null, 1, s_now, snapshot: false);

        Assert.Equal(ServerErrors.MustBeStopped, server.ChangeTemplate(current, Template("css", "1.1.0"), [], s_now).Error);
    }

    private static GameTemplate Template(string key, string version) =>
        GameTemplate.Create(key, version, 1, key, null, TemplateSource.Bundled, "{}", [1], s_now);

    private static Server Server(GameTemplate template) =>
        Domain.Servers.Server.Create(NodeId.New(), template.Id, "Retakes", null, "abcd2345", s_now).Value;
}
