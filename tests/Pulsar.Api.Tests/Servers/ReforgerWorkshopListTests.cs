using System.Collections.Concurrent;
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
using Pulsar.Domain.Access;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Mods;

namespace Pulsar.Api.Tests.Servers;

/// <summary>The panel, with Arma Reforger's Workshop faked: its pages carry their data as Next.js writes it.</summary>
public sealed class ReforgerWorkshopApiFactory : PulsarApiFactory
{
    public FakeReforgerWorkshop Workshop { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(ReforgerWorkshopClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Workshop));
    }
}

/// <summary>
/// The editor of a variable that lists Arma Reforger's Workshop mods (Pulsar 1.0.14): what each id is, and a search of
/// the Workshop, read from its public pages, for whoever creates servers or edits their startup.
/// </summary>
public sealed class ReforgerWorkshopListTests(ReforgerWorkshopApiFactory factory) : IClassFixture<ReforgerWorkshopApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TheWorkshopsModsAreFoundAndNamedFromItsPages()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        factory.Workshop.Mod("5965550F24A0C152", "Where Am I", "someone", "1.2.0", 4096);
        factory.Workshop.Mod("ABCDEF0123456789", "Barrett‮ M82", "other", "not a version!", 8192);
        factory.Workshop.Search("barrett", 2, 41, "ABCDEF0123456789", "5965550F24A0C152");

        var found = await GetAsync<ReforgerSearchView>(admin, "/api/v1/reforger-workshop/search?query=barrett&page=2");
        Assert.Equal(41, found.Total);
        Assert.Equal(["ABCDEF0123456789", "5965550F24A0C152"], found.Hits.Select(h => h.Id));
        // A name loses its direction characters; a version the Workshop would not write is left out.
        Assert.Equal(("Barrett M82", null, "https://reforger.armaplatform.com/workshop/ABCDEF0123456789"),
            (found.Hits[0].Name, found.Hits[0].Version, found.Hits[0].Url));
        Assert.Contains("/workshop?page=2&search=barrett", factory.Workshop.Requests);

        using var lookup = await admin.PostAsync("/api/v1/reforger-workshop/lookup",
            new ReforgerWorkshopLookupRequest(["5965550f24a0c152", "0000000000000001"]));
        await AccessScenario.EnsureSuccessAsync(lookup);
        var mods = (await lookup.Content.ReadFromJsonAsync<List<ReforgerModView>>(AccessScenario.Json, Ct))!;
        // The id as the list has it; the page asked for in capitals.
        Assert.Equal(("5965550f24a0c152", true, "Where Am I", "someone", "1.2.0", 4096L), (mods[0].Id, mods[0].Found, mods[0].Name, mods[0].Author, mods[0].Version,
            mods[0].Size));
        Assert.Equal("https://reforger.armaplatform.com/workshop/5965550F24A0C152", mods[0].Url);
        Assert.Equal(("0000000000000001", false, null), (mods[1].Id, mods[1].Found, mods[1].Url));
        Assert.Contains("/workshop/5965550F24A0C152", factory.Workshop.Requests);

        await ProblemAsync(await admin.PostAsync("/api/v1/reforger-workshop/lookup", new ReforgerWorkshopLookupRequest(["5965550F24A0C15"])),
            HttpStatusCode.BadRequest, ModErrors.ListInvalid.Code);
        await ProblemAsync(await admin.PostAsync("/api/v1/reforger-workshop/lookup", new ReforgerWorkshopLookupRequest(["../../workshop?x"])),
            HttpStatusCode.BadRequest, ModErrors.ListInvalid.Code);

        // Only who creates servers or changes their startup settings asks (each id is a page of the Workshop).
        using var viewer = await scenario.SignedInWithAsync("reforger-viewer", Permissions.Server.View);
        using var refused = await viewer.GetAsync("/api/v1/reforger-workshop/search?query=barrett");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [DatabaseFact]
    public async Task APageWithoutItsDataOrAnotherModsIsNotTakenForTheMod()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        factory.Workshop.Page("/workshop/1234567890ABCDEF", HttpStatusCode.OK, "<html><body>maintenance</body></html>");
        factory.Workshop.Mod("FEDCBA0987654321", "Shown for another id", "x", "1.0.0", 1, servedAs: "/workshop/AAAAAAAAAAAAAAAA");

        await ProblemAsync(await admin.PostAsync("/api/v1/reforger-workshop/lookup", new ReforgerWorkshopLookupRequest(["1234567890ABCDEF"])),
            HttpStatusCode.BadGateway, "mods.catalog_unavailable");
        using var other = await admin.PostAsync("/api/v1/reforger-workshop/lookup", new ReforgerWorkshopLookupRequest(["AAAAAAAAAAAAAAAA"]));
        await AccessScenario.EnsureSuccessAsync(other);
        Assert.False(Assert.Single((await other.Content.ReadFromJsonAsync<List<ReforgerModView>>(AccessScenario.Json, Ct))!).Found);
    }

    [DatabaseFact]
    public async Task OneUsersLookupsOfMadeUpIdsLeaveTheOthersTheWorkshop()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var greedy = await scenario.SignedInWithAsync("reforger-greedy", Permissions.Server.Create);
        using var other = await scenario.SignedInWithAsync("reforger-other", Permissions.Server.Create);
        factory.Workshop.Mod("0123456789ABCDEF", "Fair share", "x", "1.0.0", 1);
        factory.Workshop.Search("fair", 1, 1, "0123456789ABCDEF");
        static List<string> MadeUp(int from) => [.. Enumerable.Range(from, ReforgerWorkshopLists.MaxIds).Select(i => $"FF{i:X14}")];

        // Each made-up id is a page the Workshop answers with a 404: the second list goes past the user's share.
        using (var first = await greedy.PostAsync("/api/v1/reforger-workshop/lookup", new ReforgerWorkshopLookupRequest(MadeUp(0))))
        {
            await AccessScenario.EnsureSuccessAsync(first);
        }

        await ProblemAsync(await greedy.PostAsync("/api/v1/reforger-workshop/lookup", new ReforgerWorkshopLookupRequest(MadeUp(1000))),
            HttpStatusCode.ServiceUnavailable, ModErrors.CatalogBusy.Code);
        var found = await GetAsync<ReforgerSearchView>(other, "/api/v1/reforger-workshop/search?query=fair");
        Assert.Equal("Fair share", Assert.Single(found.Hits).Name);
    }

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
}

/// <summary>Arma Reforger's Workshop site: pages by path and query, each a Next.js page with its data; any other a 404.</summary>
public sealed class FakeReforgerWorkshop : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, (HttpStatusCode Status, string Html)> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonObject> _mods = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Requests { get; } = new();

    public void Page(string pathAndQuery, HttpStatusCode status, string html) => _pages[pathAndQuery] = (status, html);

    /// <summary>A mod and its page (<paramref name="servedAs"/>: the path that page is served at, when not its own).</summary>
    public void Mod(string id, string name, string author, string version, long size, string? servedAs = null)
    {
        var asset = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["type"] = "addon",
            ["author"] = new JsonObject { ["username"] = author, ["id"] = Guid.NewGuid().ToString() },
            ["currentVersionNumber"] = version,
            ["currentVersionSize"] = size,
            ["updatedAt"] = "2025-03-03T14:44:25.000Z",
            ["blocked"] = false,
            ["averageRating"] = 0.91,
        };
        _mods[id] = asset;
        Page(servedAs ?? $"/workshop/{id}", HttpStatusCode.OK, Next(new JsonObject { ["pathId"] = id, ["asset"] = asset.DeepClone() }));
    }

    public void Search(string query, int page, int count, params string[] ids) =>
        Page($"/workshop?page={page}&search={Uri.EscapeDataString(query)}", HttpStatusCode.OK, Next(new JsonObject
        {
            ["search"] = query,
            ["page"] = page,
            ["assets"] = new JsonObject { ["count"] = count, ["rows"] = new JsonArray([.. ids.Select(id => _mods[id].DeepClone())]) },
        }));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Assert.Equal("reforger.armaplatform.com", uri.Host);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Requests.Enqueue(uri.PathAndQuery);
        var (status, html) = _pages.TryGetValue(uri.PathAndQuery, out var page) ? page : (HttpStatusCode.NotFound, "<html>404</html>");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
    }

    /// <summary>A page as Next.js serves it: '&lt;' in the data escaped, so the script's end is the first "&lt;/script&gt;".</summary>
    private static string Next(JsonObject pageProps)
    {
        var data = new JsonObject { ["props"] = new JsonObject { ["pageProps"] = pageProps }, ["page"] = "/workshop", ["buildId"] = "test" };
        return $"""<html><head><script src="/x.js"></script></head><body><div id="__next"></div><script id="__NEXT_DATA__" type="application/json">{data.ToJsonString().Replace("<", "\\u003c", StringComparison.Ordinal)}</script></body></html>""";
    }
}
