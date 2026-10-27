using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Mods;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// Thunderstore faked (Pulsar 1.0.14): a community's categories and listing, packages' newest and older versions, and
/// their zips, on <c>thunderstore.io</c>.
/// </summary>
public sealed class FakeThunderstore : HttpMessageHandler
{
    /// <summary>Each package's versions, newest first, by <c>namespace-name</c>.</summary>
    public Dictionary<string, List<JsonObject>> Versions { get; } = [];

    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Requests { get; } = [];

    /// <summary>A version of a package with a zip of <paramref name="files"/>, depending on <paramref name="dependencies"/> (<c>ns-name-version</c>).</summary>
    public JsonObject Version(string package, string number, (string Path, string Content)[] files, params string[] dependencies)
    {
        var (ns, name) = (package[..package.IndexOf('-')], package[(package.IndexOf('-') + 1)..]);
        var url = $"https://thunderstore.io/package/download/{ns}/{name}/{number}/";
        Files[url] = Zip(files);
        var version = new JsonObject
        {
            ["namespace"] = ns,
            ["name"] = name,
            ["version_number"] = number,
            ["download_url"] = url,
            ["dependencies"] = new JsonArray([.. dependencies.Select(d => (JsonNode)d)]),
            ["date_created"] = "2026-10-20T10:00:00Z",
        };
        if (!Versions.TryGetValue(package, out var versions))
        {
            Versions[package] = versions = [];
        }

        versions.Insert(0, version);
        return version;
    }

    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (Requests)
        {
            Requests.Add(uri.OriginalString);
        }

        if (uri.Host != "thunderstore.io")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (Files.TryGetValue(uri.OriginalString, out var file))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        object? answer = parts switch
        {
            ["api", "cyberstorm", "community", _, "filters"] => new { package_categories = new[] { new { id = "29", slug = "server-side" } } },
            ["api", "cyberstorm", "listing", _] => Listing(System.Web.HttpUtility.ParseQueryString(uri.Query)["q"] ?? ""),
            ["api", "experimental", "package", var ns, var name] => Versions.GetValueOrDefault($"{ns}-{name}") is { } versions
                ? new JsonObject { ["namespace"] = ns, ["name"] = name, ["latest"] = versions[0].DeepClone() }
                : null,
            ["api", "experimental", "package", var ns, var name, var number] =>
                Versions.GetValueOrDefault($"{ns}-{name}")?.FirstOrDefault(v => v["version_number"]!.GetValue<string>() == number),
            _ => null,
        };
        return Task.FromResult(answer is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") });
    }

    private object Listing(string words)
    {
        var found = Versions.Where(p => p.Key.Contains(words, StringComparison.OrdinalIgnoreCase)).Select(p => new
        {
            @namespace = p.Value[0]["namespace"]!.GetValue<string>(),
            name = p.Value[0]["name"]!.GetValue<string>(),
            description = $"{p.Key}, for tests",
            download_count = 1000,
            last_updated = "2026-10-20T10:00:00Z",
            is_deprecated = false,
            is_nsfw = false,
        }).ToList();
        return new { count = found.Count, next = (string?)null, results = found };
    }
}

/// <summary>
/// The panel with the test game made to take Thunderstore's packages on a BepInEx addon from Thunderstore (a copy of
/// <c>games/_test</c>, in a folder of its own), and Thunderstore faked for the panel and the node.
/// </summary>
public sealed class ThunderstoreApiFactory : PulsarApiFactory
{
    private readonly string _games = Directory.CreateTempSubdirectory("pulsar-thunderstore-games-").FullName;

    public FakeThunderstore Thunderstore { get; } = new();

    public ThunderstoreApiFactory()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "games", "_test", "manifest.json")))!.AsObject();
        manifest["addons"] = JsonNode.Parse("""
            [ { "key": "bepinex", "name": "BepInEx (test)", "license": "LGPL-2.1", "url": "https://thunderstore.io/",
                "source": { "type": "thunderstore", "package": "denikson-BepInExPack_Valheim", "folder": "BepInExPack_Valheim" } } ]
            """);
        manifest["mods"] = JsonNode.Parse("""
            [ { "source": "thunderstore", "community": "valheim", "category": "server-side", "addonTypes": true,
                "types": { "bepinex": { "projectType": "mod", "folder": "BepInEx", "layout": "bepinex" } } } ]
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
            services.AddHttpClient(ThunderstoreClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Thunderstore);
        });
    }
}

/// <summary>
/// Thunderstore's packages (Pulsar 1.0.14) through the panel and the real agent core: BepInEx installed as an addon from
/// Thunderstore, then packages of the community's server-side category installed with the packages they depend on (the
/// BepInEx pack they all name is the addon), into BepInEx's folder while the server is stopped, updated and removed.
/// </summary>
public sealed class ServerThunderstoreTests(ThunderstoreApiFactory factory) : IClassFixture<ThunderstoreApiFactory>
{
    private const string Pack = "denikson-BepInExPack_Valheim-5.4.2351";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task PackagesComeOnBepInExWithWhatTheyNeedUpdatedAndRemoved()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "flow");
        var server = await InstalledAsync(admin, agent, "Packages");
        var root = Path.Combine(agent.ServersRoot, (await ServerApi.GetAsync(admin, server))!.Server.ShortId);
        Assert.Equal(ModErrors.AddonMissing.Code, (await ModsAsync(admin, server)).Unavailable);

        // BepInEx, from Thunderstore by the node: its pack's own folder of the zip.
        factory.Thunderstore.Version("denikson-BepInExPack_Valheim", "5.4.2351",
            [("manifest.json", "{}"), ("BepInExPack_Valheim/winhttp.dll", "doorstop"), ("BepInExPack_Valheim/BepInEx/core/BepInEx.dll", "core")]);
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/addons/bepinex", new { }));
        await ServerApi.WaitForAsync(admin, server, s => s.Server.ActiveOperation is null, "BepInEx installed");
        Assert.Equal("core", await File.ReadAllTextAsync(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"), Ct));
        var view = await ModsAsync(admin, server);
        Assert.Equal(("mod", "BepInEx", null, "valheim"), (view.ProjectType, view.Folder, view.Unavailable, view.Community));

        factory.Thunderstore.Version("ValheimModding-Jotunn", "2.30.0", [("plugins/Jotunn.dll", "jotunn 2.30.0")], Pack);
        factory.Thunderstore.Version("ValheimModding-Jotunn", "2.30.2", [("plugins/Jotunn.dll", "jotunn 2.30.2")], Pack);
        factory.Thunderstore.Version("Advize-PlantEverything", "1.19.0", [("PlantEverything.dll", "plant 1.19.0"), ("config/advize.cfg", "x=1")],
            Pack, "ValheimModding-Jotunn-2.30.0");
        var found = await GetAsync<ModSearchView>(admin, $"/api/v1/servers/{server}/mods/search?source=thunderstore&query=Plant");
        Assert.Equal(("Advize-PlantEverything", "PlantEverything"), (Assert.Single(found.Hits).ProjectId, found.Hits[0].Title));
        Assert.Contains(factory.Thunderstore.Requests, r => r.Contains("/listing/valheim/", StringComparison.Ordinal)
            && r.Contains("included_categories=29", StringComparison.Ordinal));

        // A package needs the server stopped (the game holds its files), as other catalogs' mods do.
        await ServerApi.CommandAsync(admin, server, "start");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        await ProblemAsync(await admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("Advize-PlantEverything", null, "thunderstore")),
            HttpStatusCode.Conflict, ServerErrors.MustBeStopped.Code);
        await ServerApi.CommandAsync(admin, server, "stop");
        await ServerApi.WaitForAsync(admin, server, s => s.Server.RuntimeState == ServerRuntimeState.Offline && s.Server.ActiveOperation is null, "stopped");

        // With the version of Jotunn it names; the BepInEx pack it names is the addon, not a package to install.
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods", new InstallModRequest("Advize-PlantEverything", null, "thunderstore")));
        view = await SettledAsync(admin, server, mods => mods.Count == 2);
        var plant = view.Installed.Single(m => m.ProjectId == "Advize-PlantEverything");
        var jotunn = view.Installed.Single(m => m.ProjectId == "ValheimModding-Jotunn");
        Assert.Equal(("thunderstore", "1.19.0", "BepInEx/plugins/Advize-PlantEverything", false), (plant.Source, plant.VersionNumber, plant.Path, plant.Dependency));
        Assert.Equal(("2.30.0", true, "PlantEverything"), (jotunn.VersionNumber, jotunn.Dependency, Assert.Single(jotunn.RequiredBy)));
        Assert.Equal("plant 1.19.0", await File.ReadAllTextAsync(Path.Combine(root, "BepInEx", "plugins", "Advize-PlantEverything", "PlantEverything.dll"), Ct));
        Assert.Equal("jotunn 2.30.0", await File.ReadAllTextAsync(Path.Combine(root, "BepInEx", "plugins", "ValheimModding-Jotunn", "Jotunn.dll"), Ct));
        Assert.Equal("x=1", await File.ReadAllTextAsync(Path.Combine(root, "BepInEx", "config", "advize.cfg"), Ct));

        // Updates: each package's newest version, put in place of the old one.
        var updates = await GetAsync<ModUpdateList>(admin, $"/api/v1/servers/{server}/mods/updates");
        Assert.Equal(("2.30.0", "2.30.2"), (Assert.Single(updates.Updates).VersionNumber, updates.Updates[0].NewVersionNumber));
        await ChangeAsync(admin.PostAsync($"/api/v1/servers/{server}/mods/update", new UpdateModsRequest(null)));
        view = await SettledAsync(admin, server, list => list.Any(m => m.VersionNumber == "2.30.2"));
        Assert.Equal("jotunn 2.30.2", await File.ReadAllTextAsync(Path.Combine(root, "BepInEx", "plugins", "ValheimModding-Jotunn", "Jotunn.dll"), Ct));

        // Removed: PlantEverything, then what it needed; its configuration stays the server's.
        jotunn = view.Installed.Single(m => m.ProjectId == "ValheimModding-Jotunn"); // the update's row
        await ProblemAsync(await admin.DeleteAsync($"/api/v1/servers/{server}/mods/{jotunn.Id}"), HttpStatusCode.Conflict, "mods.required_by");
        await ChangeAsync(admin.DeleteAsync($"/api/v1/servers/{server}/mods/{plant.Id}"));
        view = await SettledAsync(admin, server, list => list.Count == 1);
        await ChangeAsync(admin.DeleteAsync($"/api/v1/servers/{server}/mods/{view.Installed.Single().Id}"));
        await SettledAsync(admin, server, list => list.Count == 0);
        Assert.False(Directory.Exists(Path.Combine(root, "BepInEx", "plugins", "Advize-PlantEverything")));
        Assert.False(Directory.Exists(Path.Combine(root, "BepInEx", "plugins", "ValheimModding-Jotunn")));
        Assert.True(File.Exists(Path.Combine(root, "BepInEx", "config", "advize.cfg")));
    }

    private static async Task ChangeAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        await AccessScenario.EnsureSuccessAsync(response);
    }

    /// <summary>Waits until no change is running and the installed packages match.</summary>
    private static async Task<ModsView> SettledAsync(PanelClient client, Guid server, Func<IReadOnlyList<ServerModView>, bool> expected)
    {
        ModsView? view = null;
        await InProcessAgent.EventuallyAsync(async () =>
        {
            view = await ModsAsync(client, server);
            var details = await ServerApi.GetAsync(client, server);
            return details!.Server.ActiveOperation is null && view.Installed.All(m => m.State == ServerModState.Installed) && expected(view.Installed);
        }, () => $"packages settled (last: {string.Join(", ", view?.Installed.Select(m => $"{m.ProjectId} {m.VersionNumber} {m.State}") ?? [])})");
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
        var created = await NodeApi.CreateAsync(admin, $"thunderstore-{name}");
        var agent = new InProcessAgent(factory) { Http = factory.Thunderstore };
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }
}
