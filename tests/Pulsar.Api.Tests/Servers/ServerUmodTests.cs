using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Mods;

namespace Pulsar.Api.Tests.Servers;

/// <summary>uMod faked (Pulsar 1.0.14): its search, its plugins' pages and the plugins' files, on <c>umod.org</c>.</summary>
public sealed class FakeUmod : HttpMessageHandler
{
    /// <summary>Each plugin's page, as uMod's JSON, by slug.</summary>
    public Dictionary<string, JsonObject> Plugins { get; } = [];

    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Requests { get; } = [];

    /// <summary>A plugin uMod gives out, its newest release <paramref name="version"/> with <paramref name="content"/>.</summary>
    public JsonObject Plugin(string slug, string name, string title, string version, byte[] content, string distribution = "download")
    {
        var url = $"https://umod.org/plugins/{name}.cs";
        Files[url] = content;
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(content);
        var plugin = new JsonObject
        {
            ["name"] = name,
            ["title"] = title,
            ["slug"] = slug,
            ["description"] = $"{title}, for tests",
            ["author"] = "tester",
            ["downloads"] = 1000,
            ["distribution"] = distribution,
            ["download_url"] = url,
            ["latest_release_version"] = version,
            ["latest_release_version_checksum"] = Convert.ToHexStringLower(sha1.GetHashAndReset()),
            ["latest_release_at_atom"] = "2026-10-20T10:00:00+00:00",
            ["updated_at_atom"] = "2026-10-20T10:00:00Z",
        };
        Plugins[slug] = plugin;
        return plugin;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (Requests)
        {
            Requests.Add(uri.OriginalString);
        }

        if (uri.Host != "umod.org")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (Files.TryGetValue(uri.OriginalString, out var file))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
        }

        object? answer = null;
        if (uri.AbsolutePath == "/plugins/search.json")
        {
            // As uMod does: ten a page, numbered from 1.
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var words = query["query"] ?? "";
            var page = int.Parse(query["page"] ?? "1", System.Globalization.CultureInfo.InvariantCulture);
            var found = Plugins.Values.Where(p => p["title"]!.GetValue<string>().Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
            answer = new
            {
                current_page = page,
                data = found.Skip((page - 1) * 10).Take(10),
                last_page = Math.Max(1, (found.Count + 9) / 10),
                per_page = 10,
                total = found.Count
            };
        }
        else if (uri.AbsolutePath.StartsWith("/plugins/", StringComparison.Ordinal) && uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
        {
            answer = Plugins.GetValueOrDefault(uri.AbsolutePath["/plugins/".Length..^".json".Length]);
        }

        return Task.FromResult(answer is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") });
    }
}

/// <summary>
/// The panel with the test game made to take uMod's plugins on an addon (a copy of <c>games/_test</c>, in a folder of its
/// own), and uMod faked.
/// </summary>
public sealed class UmodApiFactory : PulsarApiFactory
{
    private readonly string _games = Directory.CreateTempSubdirectory("pulsar-umod-games-").FullName;

    public FakeUmod Umod { get; } = new();

    public UmodApiFactory()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "games", "_test", "manifest.json")))!.AsObject();
        manifest["addons"] = JsonNode.Parse("""
            [ { "key": "oxide", "name": "Oxide (test)", "license": "MIT", "url": "https://umod.org/",
                "description": "Plugins in C#.", "source": { "type": "githubRelease", "repo": "OxideMod/Oxide.Rust", "asset": "^Oxide\\.Rust\\.zip$" },
                "conflicts": ["carbon"] },
              { "key": "carbon", "name": "Carbon (test)", "license": "GPL-3.0", "url": "https://carbonmod.gg/",
                "description": "Plugins in C#.", "source": { "type": "githubRelease", "repo": "CarbonCommunity/Carbon", "asset": "^Carbon\\.Windows\\.Release\\.zip$" },
                "conflicts": ["oxide"] } ]
            """);
        manifest["mods"] = JsonNode.Parse("""
            [ { "source": "umod", "community": "rust", "addonTypes": true, "whileRunning": true,
                "types": { "oxide": { "projectType": "plugin", "folder": "oxide/plugins", "extensions": [".cs"] },
                           "carbon": { "projectType": "plugin", "folder": "carbon/plugins", "extensions": [".cs"] } } } ]
            """);
        Directory.CreateDirectory(Path.Combine(_games, "_test"));
        File.WriteAllText(Path.Combine(_games, "_test", "manifest.json"), manifest.ToJsonString());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_games, recursive: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new TemplateOptions { EnableTestGame = true, BundledDirectory = _games });
            services.AddHttpClient(UmodClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Umod);
        });
    }
}

/// <summary>
/// uMod's plugins (Pulsar 1.0.14) through the panel and the real agent core: offered once the server has the addon they
/// run on, put into its folder while the game runs (Oxide loads them as they come), each checked against uMod's SHA-1.
/// </summary>
public sealed class ServerUmodTests(UmodApiFactory factory) : IClassFixture<UmodApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task PluginsComeOnceTheAddonIsThereAndChangeWhileTheGameRuns()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "flow");
        var server = await InstalledAsync(admin, agent, "Plugins");

        var view = await ModsAsync(admin, server);
        Assert.Equal((true, ModErrors.AddonMissing.Code), (view.Supported, view.Unavailable));
        Assert.Equal(["umod"], view.Catalogs);
        await ProblemAsync(await admin.GetAsync($"/api/v1/servers/{server}/mods/search?source=umod&query=kits"), HttpStatusCode.Conflict,
            ModErrors.AddonMissing.Code);

        await AddonInstalledAsync(server, "oxide");
        view = await ModsAsync(admin, server);
        Assert.Equal(("plugin", "oxide/plugins", null, null, true), (view.ProjectType, view.Folder, view.GameVersion, view.Unavailable, view.WhileRunning));

        var kits = Encoding.UTF8.GetBytes("// Kits 4.4.10");
        factory.Umod.Plugin("rust-kits", "Kits", "Rust Kits", "4.4.10", kits);
        factory.Umod.Plugin("external-plugin", "External", "External kits", "1.0.0", Encoding.UTF8.GetBytes("elsewhere"), distribution: "external");
        var found = await GetAsync<ModSearchView>(admin, $"/api/v1/servers/{server}/mods/search?source=umod&query=kits");
        Assert.Equal(["External kits", "Rust Kits"], found.Hits.Select(h => h.Title).Order(StringComparer.Ordinal));
        Assert.Contains(factory.Umod.Requests, r => r.Contains("search.json", StringComparison.Ordinal) && r.Contains("categories%5B%5D=rust", StringComparison.Ordinal));

        // Installed while the game runs: Oxide loads a plugin as it comes.
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("rust-kits", null, "umod")));
        view = await SettledAsync(admin, server, mods => mods.Count == 1);
        var mod = Assert.Single(view.Installed);
        Assert.Equal(("umod", "Rust Kits", "4.4.10", "oxide/plugins/Kits.cs"), (mod.Source, mod.Name, mod.VersionNumber, mod.Path));
        var plugins = Path.Combine(agent.ServersRoot, (await ServerApi.GetAsync(admin, server))!.Server.ShortId, "oxide", "plugins");
        Assert.Equal(kits, await File.ReadAllBytesAsync(Path.Combine(plugins, "Kits.cs"), Ct));
        Assert.Equal(ServerRuntimeState.Running, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);

        // A plugin uMod links elsewhere has nothing the node can take.
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("external-plugin", null, "umod")),
            HttpStatusCode.BadRequest, ModErrors.NoVersion.Code);

        // A newer release, found on the plugin's page, put in place of the old one.
        var newer = Encoding.UTF8.GetBytes("// Kits 4.4.11");
        factory.Umod.Plugin("rust-kits", "Kits", "Rust Kits", "4.4.11", newer);
        var updates = await GetAsync<ModUpdateList>(admin, $"/api/v1/servers/{server}/mods/updates");
        Assert.Equal(("4.4.10", "4.4.11"), (Assert.Single(updates.Updates).VersionNumber, updates.Updates[0].NewVersionNumber));
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods/update", new UpdateModsRequest(null)));
        view = await SettledAsync(admin, server, list => list.Any(m => m.VersionNumber == "4.4.11"));
        Assert.Equal(newer, await File.ReadAllBytesAsync(Path.Combine(plugins, "Kits.cs"), Ct));

        // A file that is not what uMod says changes nothing.
        factory.Umod.Plugin("rust-kits", "Kits", "Rust Kits", "4.4.12", Encoding.UTF8.GetBytes("// Kits 4.4.12"));
        factory.Umod.Files["https://umod.org/plugins/Kits.cs"] = Encoding.UTF8.GetBytes("tampered");
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods/update", new UpdateModsRequest(null)));
        await SettledAsync(admin, server, list => list.Single().VersionNumber == "4.4.11");
        var details = await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "the change ended");
        Assert.Contains(details.RecentOperations, o => o.Type == OperationType.ModsChange && o.ErrorCode == "mods.checksum_mismatch");
        Assert.Equal(newer, await File.ReadAllBytesAsync(Path.Combine(plugins, "Kits.cs"), Ct));

        // Removed, the game still running.
        await ChangeAsync(admin.DeleteAsync($"/api/v1/servers/{server}/mods/{view.Installed.Single().Id}"));
        await SettledAsync(admin, server, list => list.Count == 0);
        Assert.False(File.Exists(Path.Combine(plugins, "Kits.cs")));
        Assert.Equal(ServerRuntimeState.Running, (await ServerApi.GetAsync(admin, server))!.Server.RuntimeState);
    }

    [DatabaseFact]
    public async Task UmodsPagesOfTenMakeThePanelsPagesOfTwenty()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "pages");
        var server = await InstalledAsync(admin, agent, "Pages");
        await AddonInstalledAsync(server, "oxide");
        for (var i = 0; i < 25; i++)
        {
            factory.Umod.Plugin($"paged-{i:00}", $"Paged{i:00}", $"Paged {i:00}", "1.0.0", Encoding.UTF8.GetBytes($"paged {i}"));
        }

        // One uMod lists with a slug Pulsar does not take: it is left out, and the pages after it do not shift.
        factory.Umod.Plugins["paged-03"]!["slug"] = "Paged 03";

        Assert.Equal(Titles(0, 20, skip: 3), await PageAsync(admin, server, 0));
        Assert.Equal(Titles(20, 25), await PageAsync(admin, server, 20));
        Assert.Equal(Titles(5, 25), await PageAsync(admin, server, 5));
        var all = await GetAsync<ModSearchView>(admin, $"/api/v1/servers/{server}/mods/search?source=umod&query=Paged&offset=0");
        Assert.Equal(25, all.Total);

        static string[] Titles(int from, int to, int skip = -1) =>
            [.. Enumerable.Range(from, to - from).Where(i => i != skip).Select(i => $"Paged {i:00}")];
    }

    [DatabaseFact]
    public async Task CarbonsPluginsGoIntoItsFolderAndUpdatingAllPassesOverOnesWhoseAddonIsGone()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "carbon");
        var server = await InstalledAsync(admin, agent, "Carbon");
        await AddonInstalledAsync(server, "carbon");
        Assert.Equal("carbon/plugins", (await ModsAsync(admin, server)).Folder);
        var gather = Encoding.UTF8.GetBytes("// Gather 1.0.0");
        factory.Umod.Plugin("gather-manager", "GatherManager", "Gather Manager", "1.0.0", gather);

        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("gather-manager", null, "umod")));
        var view = await SettledAsync(admin, server, mods => mods.Count == 1);
        Assert.Equal("carbon/plugins/GatherManager.cs", view.Installed.Single().Path);
        var shortId = (await ServerApi.GetAsync(admin, server))!.Server.ShortId;
        Assert.Equal(gather, await File.ReadAllBytesAsync(Path.Combine(agent.ServersRoot, shortId, "carbon", "plugins", "GatherManager.cs"), Ct));

        // Carbon removed: its plugins stay recorded, and updating them all has nothing to do rather than failing.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            db.ServerAddons.RemoveRange(await db.ServerAddons.Where(a => a.ServerId == new ServerId(server)).ToListAsync(Ct));
            await db.SaveChangesAsync(Ct);
        }

        factory.Umod.Plugin("gather-manager", "GatherManager", "Gather Manager", "1.0.1", Encoding.UTF8.GetBytes("// Gather 1.0.1"));
        Assert.Equal(ModErrors.AddonMissing.Code, (await ModsAsync(admin, server)).Unavailable);
        Assert.Empty((await GetAsync<ModUpdateList>(admin, $"/api/v1/servers/{server}/mods/updates")).Updates);
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/mods/update", new UpdateModsRequest(null)), HttpStatusCode.BadRequest,
            ModErrors.NoUpdates.Code);
    }

    private static async Task<string[]> PageAsync(PanelClient client, Guid server, int offset) =>
        [.. (await GetAsync<ModSearchView>(client, $"/api/v1/servers/{server}/mods/search?source=umod&query=Paged&offset={offset}")).Hits.Select(h => h.Title)];

    private async Task AddonInstalledAsync(Guid server, string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
        db.ServerAddons.Add(ServerAddon.Installed(new ServerId(server), key, "v2.0.0", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task ChangeAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        await AccessScenario.EnsureSuccessAsync(response);
    }

    /// <summary>Waits until no change is running and the installed plugins match.</summary>
    private static async Task<ModsView> SettledAsync(PanelClient client, Guid server, Func<IReadOnlyList<ServerModView>, bool> expected)
    {
        ModsView? view = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            view = await ModsAsync(client, server);
            var details = await ServerApi.GetAsync(client, server);
            return details!.Server.ActiveOperation is null && view.Installed.All(m => m.State == ServerModState.Installed) && expected(view.Installed);
        }, () => $"plugins settled (last: {string.Join(", ", view?.Installed.Select(m => $"{m.ProjectId} {m.VersionNumber} {m.State}") ?? [])})");
        return view!;
    }

    private static Task<ModsView> ModsAsync(PanelClient client, Guid server) => GetAsync<ModsView>(client, $"/api/v1/servers/{server}/mods");

    private static async Task<T> GetAsync<T>(PanelClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        }
    }

    private static async Task<Guid> InstalledAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return created.Server.Id;
    }

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, $"umod-{name}");
        var agent = new InProcessAgent(factory) { Http = factory.Umod };
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
