using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// The editor of a variable that lists CurseForge projects (Pulsar 1.0.14; ARK: Survival Ascended's mods): a search of
/// the game's mods and what each id is, with the administrator's key, for whoever creates servers or edits their startup.
/// </summary>
public sealed class CurseForgeListTests(ModrinthApiFactory factory) : IClassFixture<ModrinthApiFactory>
{
    private const int Ark = 83374;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TheGamesModsAreFoundAndNamedWithTheAdministratorsKey()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;

        // Without the key CurseForge cannot be asked: the editor adds ids as typed.
        await ProblemAsync(await admin.PostAsync("/api/v1/curseforge/lookup", new CurseForgeLookupRequest(Ark, ["928548"])), HttpStatusCode.Conflict,
            ModErrors.CatalogNotConfigured.Code);
        using (var set = await admin.PutAsync("/api/v1/mod-catalogs/curseforge-key", new CurseForgeKeyRequest(FakeCurseForge.Key)))
        {
            await AccessScenario.EnsureSuccessAsync(set);
        }

        factory.Modrinth.CurseForge.Mod(928548, "Super Spyglass", classId: 4545, gameId: Ark);
        factory.Modrinth.CurseForge.Mod(929420, "Awesome Teleporters", classId: 4545, gameId: Ark);
        factory.Modrinth.CurseForge.Mod(238222, "Just Enough Items");

        var found = await GetAsync<CurseForgeSearchView>(admin, $"/api/v1/curseforge/search?gameId={Ark}&query=spy");
        var hit = Assert.Single(found.Hits);
        Assert.Equal(("928548", "Super Spyglass", false, "someone"), (hit.Id, hit.Name, hit.OtherGame, hit.Author));
        Assert.Contains(factory.Modrinth.Requests, r => r.Contains($"gameId={Ark}", StringComparison.Ordinal) && r.Contains("searchFilter=spy", StringComparison.Ordinal));

        using var lookup = await admin.PostAsync("/api/v1/curseforge/lookup", new CurseForgeLookupRequest(Ark, ["928548", "238222", "999999"]));
        await AccessScenario.EnsureSuccessAsync(lookup);
        var projects = (await lookup.Content.ReadFromJsonAsync<List<CurseForgeProjectView>>(AccessScenario.Json, Ct))!;
        Assert.Equal(("Super Spyglass", true, false, "https://www.curseforge.com/game-83374/mods/super-spyglass"),
            (projects[0].Name, projects[0].Found, projects[0].OtherGame, projects[0].Url));
        Assert.True(projects[1].OtherGame); // Minecraft's, not ARK's
        Assert.False(projects[2].Found);

        await ProblemAsync(await admin.PostAsync("/api/v1/curseforge/lookup", new CurseForgeLookupRequest(Ark, ["0123"])), HttpStatusCode.BadRequest,
            ModErrors.ListInvalid.Code);

        // Only who creates servers or changes their startup settings asks (each search is a request with the administrator's key).
        using var viewer = await scenario.SignedInWithAsync("lists-viewer", Permissions.Server.View);
        using var refused = await viewer.GetAsync($"/api/v1/curseforge/search?gameId={Ark}&query=spy");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
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
