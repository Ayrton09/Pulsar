using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Nodes of the Linux agent beside Windows ones: each says its platform, and a server goes only where its game runs,
/// and moves or is copied only to a node of the same platform (its files are made for it).
/// </summary>
public sealed class NodePlatformTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ANodeSaysItsPlatformAndAnAgentThatSaysNoneIsAWindowsOne()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var (linuxNode, linux) = await ConnectedAsync(admin, "platform-linux", "linux-x64");
        await using var _ = linux;
        var (windowsNode, windows) = await ConnectedAsync(admin, "platform-before-linux", platform: null);
        await using var __ = windows;

        Assert.Equal("linux-x64", (await NodeApi.GetAsync(admin, linuxNode)).Node.Platform);
        Assert.Equal("windows-x64", (await NodeApi.GetAsync(admin, windowsNode)).Node.Platform);
    }

    [DatabaseFact]
    public async Task TemplatesSayWhereTheyRun()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var templates = (await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct))!;

        Assert.Equal(["windows-x64", "linux-x64"], templates.Single(t => t.IsTestGame).Platforms);
        Assert.Equal(["windows-x64", "linux-x64"], templates.Last(t => t.Key == "css").Platforms);
        Assert.Equal(["windows-x64"], templates.Last(t => t.Key == "ao2027").Platforms);
    }

    [DatabaseFact]
    public async Task AWindowsOnlyGameIsNotCreatedOnALinuxNode()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var (node, agent) = await ConnectedAsync(admin, "platform-no-ao", "linux-x64");
        await using var _ = agent;
        await GameServerTests.AllocateAsync(admin, node, "0.0.0.0", "7666-7672");
        var ao = await TemplateAsync(admin, "ao2027");

        using var response = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node, ao.Id, "Not on Linux", null));

        await Auth.SetupTests.AssertProblemAsync(response, HttpStatusCode.Conflict, "server.platform_unsupported");
    }

    [DatabaseFact]
    public async Task AGameForLinuxIsCreatedOnALinuxNode()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var (node, agent) = await ConnectedAsync(admin, "platform-test-game", "linux-x64");
        await using var _ = agent;

        var created = await ServerApi.CreateAsync(admin, node, "On Linux");

        Assert.Equal(node, created.Server.NodeId);
        await InProcessAgent.EventuallyAsync(() =>
        {
            lock (agent.ReceivedCommands)
            {
                return Task.FromResult(agent.ReceivedCommands.Any(c => c.Command is ProvisionServerCommand));
            }
        }, "provision sent");
    }

    [DatabaseFact]
    public async Task AGameRunWithWineIsCreatedOnlyOnALinuxNodeWithWine()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var (bare, bareAgent) = await ConnectedAsync(admin, "platform-no-wine", "linux-x64", without: AgentCapabilities.Wine);
        await using var _ = bareAgent;
        var (wine, wineAgent) = await ConnectedAsync(admin, "platform-wine", "linux-x64");
        await using var __ = wineAgent;
        var (windows, windowsAgent) = await ConnectedAsync(admin, "platform-wine-windows", "windows-x64", without: AgentCapabilities.Wine);
        await using var ___ = windowsAgent;
        foreach (var node in (Guid[])[bare, wine, windows])
        {
            await GameServerTests.AllocateAsync(admin, node, "0.0.0.0", "9876-9878");
            await GameServerTests.AllocateAsync(admin, node, "127.0.0.1", "25575");
        }

        var vrising = await TemplateAsync(admin, "vrising");

        using var refused = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(bare, vrising.Id, "No Wine here", null));
        await Auth.SetupTests.AssertProblemAsync(refused, HttpStatusCode.Conflict, "server.template_unsupported");
        using var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(wine, vrising.Id, "With Wine", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // A Windows node runs the game itself: it needs no Wine.
        using var onWindows = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(windows, vrising.Id, "On Windows", null));
        Assert.Equal(HttpStatusCode.Created, onWindows.StatusCode);
    }

    [DatabaseFact]
    public async Task EachNodeIsSentItsOwnPlatformsManifest()
    {
        // An agent released before the Linux agent refuses a manifest with members it does not know: a Windows node is
        // sent the Windows runtime alone.
        using var admin = await factory.CreateAdminClientAsync();
        var (windowsNode, windows) = await ConnectedAsync(admin, "platform-sent-windows", "windows-x64");
        await using var _ = windows;
        var (linuxNode, linux) = await ConnectedAsync(admin, "platform-sent-linux", "linux-x64");
        await using var __ = linux;

        await ServerApi.CreateAsync(admin, windowsNode, "Sent to Windows");
        await ServerApi.CreateAsync(admin, linuxNode, "Sent to Linux");

        var toWindows = await ProvisionedManifestAsync(windows);
        var toLinux = await ProvisionedManifestAsync(linux);
        Assert.Contains("\"windows-x64\"", toWindows, StringComparison.Ordinal);
        Assert.DoesNotContain("linux-x64", toWindows, StringComparison.Ordinal);
        Assert.Contains("\"linux-x64\"", toLinux, StringComparison.Ordinal);
        Assert.DoesNotContain("windows-x64", toLinux, StringComparison.Ordinal);
    }

    private static async Task<string> ProvisionedManifestAsync(TestAgent agent)
    {
        string? manifest = null;
        await InProcessAgent.EventuallyAsync(() =>
        {
            lock (agent.ReceivedCommands)
            {
                manifest = agent.ReceivedCommands.Select(c => c.Command).OfType<ProvisionServerCommand>().FirstOrDefault()?.Spec.Manifest;
            }

            return Task.FromResult(manifest is not null);
        }, "provision sent");
        return manifest!;
    }

    [DatabaseFact]
    public async Task AServerDoesNotMoveOrCopyToANodeOfAnotherPlatform()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var (windowsNode, windows) = await ConnectedAsync(admin, "platform-from", "windows-x64");
        await using var _ = windows;
        var (linuxNode, linux) = await ConnectedAsync(admin, "platform-to", "linux-x64");
        await using var __ = linux;
        var server = await ServerApi.CreateAsync(admin, windowsNode, "Stays on Windows");

        using (var moving = await admin.PostAsync($"/api/v1/servers/{server.Server.Id}/transfer", new TransferServerRequest(linuxNode)))
        {
            await Auth.SetupTests.AssertProblemAsync(moving, HttpStatusCode.Conflict, "server.other_platform");
        }

        using (var copying = await admin.PostAsync($"/api/v1/servers/{server.Server.Id}/clone", new CloneServerRequest(linuxNode, "Copy on Linux")))
        {
            await Auth.SetupTests.AssertProblemAsync(copying, HttpStatusCode.Conflict, "server.other_platform");
        }
    }

    [DatabaseFact]
    public async Task AServerDoesNotTakeAVersionOfItsGameThatNoLongerRunsOnItsNode()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var (node, agent) = await ConnectedAsync(admin, "platform-upgrade", "linux-x64");
        await using var _ = agent;
        var both = await ImportAsync(admin, "1.0.0", linux: true);
        var windowsOnly = await ImportAsync(admin, "1.1.0", linux: false);
        Guid server;
        using (var created = await admin.PostAsync("/api/v1/servers", new CreateServerRequest(node, both, "Linux check", null)))
        {
            await AccessScenario.EnsureSuccessAsync(created);
            server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.Id;
        }

        using var upgrade = await admin.PutAsync($"/api/v1/servers/{server}/template", new UpgradeTemplateRequest(windowsOnly));

        await Auth.SetupTests.AssertProblemAsync(upgrade, HttpStatusCode.Conflict, "server.platform_unsupported");
    }

    /// <summary>A game like the test one, under a key of its own (the test game's cannot be imported).</summary>
    private static async Task<Guid> ImportAsync(PanelClient admin, string version, bool linux)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "games", "_test", "manifest.json"), Ct))!.AsObject();
        manifest["key"] = "platform-check";
        manifest["version"] = version;
        manifest["install"]!["steps"] = new JsonArray(new JsonObject { ["type"] = "ensureDirectory", ["path"] = "data" });
        if (!linux)
        {
            manifest["platforms"] = new JsonArray("windows-x64");
            manifest["runtime"]!.AsObject().Remove("linux-x64");
        }

        using var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest.ToJsonString()));
        await AccessScenario.EnsureSuccessAsync(imported);
        return (await imported.Content.ReadFromJsonAsync<TemplateDetails>(AccessScenario.Json, Ct))!.Template.Id;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    private async Task<(Guid Node, TestAgent Agent)> ConnectedAsync(PanelClient admin, string name, string? platform, string? without = null)
    {
        var node = await NodeApi.CreateAsync(admin, name);
        var agent = new TestAgent(factory) { HelloInventory = TestAgent.Inventory with { Platform = platform } };
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        // Everything an agent of this version can do: only the platform tells the nodes apart.
        string[] capabilities = [.. typeof(AgentCapabilities).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Where(c => c != without)];
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken, capabilities);
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        return (node.Node.Id, agent);
    }

    private static async Task<TemplateView> TemplateAsync(PanelClient admin, string key)
    {
        var templates = (await admin.Http.GetFromJsonAsync<List<TemplateView>>(new Uri("/api/v1/templates", UriKind.Relative), AccessScenario.Json, Ct))!;
        return templates.Last(t => t.Key == key);
    }
}
