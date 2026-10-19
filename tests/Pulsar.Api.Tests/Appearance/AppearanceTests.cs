using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Appearance;
using Pulsar.Api.Security;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Appearance;

/// <summary>
/// The panel-wide Steam artwork switch (docs/architecture/08, T41): any signed-in user reads it, <c>settings.manage</c>
/// changes it (audited), and the Content-Security-Policy names Steam's CDN only while it is on.
/// </summary>
public sealed class AppearanceTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AnySignedInUserReadsItAndOnlySettingsManageChangesIt()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        using var user = await scenario.SignedInWithAsync("appearance-user");
        var before = await ReadAsync(user);
        var audited = (await AuditAsync()).Count;

        using (var refused = await user.PutAsync("/api/v1/appearance", new UpdateAppearanceSettingsRequest(!before.SteamArtwork)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(before, await ReadAsync(user));
        Assert.Equal(audited, (await AuditAsync()).Count);
    }

    [DatabaseFact]
    public async Task TurningTheArtworkOffTakesSteamOutOfTheContentSecurityPolicy()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        using var user = await scenario.SignedInWithAsync("appearance-viewer");
        using var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        // No row yet: on, as before the setting existed.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AppearanceSettings.AnyAsync(Ct));
        }

        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithSteamArtwork, await PolicyAsync(visitor));
        Assert.True((await ReadAsync(user)).SteamArtwork);

        // Off: the answer and every response after it, anyone's, name no Steam origin, and open pages hear of it.
        await using var page = await BrowserHub.ConnectAsync(factory, user);
        using (var off = await admin.PutAsync("/api/v1/appearance", new UpdateAppearanceSettingsRequest(false)))
        {
            await AccessScenario.EnsureSuccessAsync(off);
            Assert.False((await off.Content.ReadFromJsonAsync<AppearanceSettingsResponse>(AccessScenario.Json, Ct))!.SteamArtwork);
            Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, PolicyOf(off));
        }

        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, await PolicyAsync(visitor));
        Assert.False((await ReadAsync(user)).SteamArtwork);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithoutSteamArtwork, await PolicyAfterRestartAsync());

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(page.AppearanceChanged == 1), "the open page hears of the change");

        // The same value again changes nothing, is not audited and tells no page.
        using (var same = await admin.PutAsync("/api/v1/appearance", new UpdateAppearanceSettingsRequest(false)))
        {
            await AccessScenario.EnsureSuccessAsync(same);
        }

        Assert.Equal(1, page.AppearanceChanged);

        using (var on = await admin.PutAsync("/api/v1/appearance", new UpdateAppearanceSettingsRequest(true)))
        {
            await AccessScenario.EnsureSuccessAsync(on);
            Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithSteamArtwork, PolicyOf(on));
        }

        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithSteamArtwork, await PolicyAsync(visitor));
        Assert.True((await ReadAsync(user)).SteamArtwork);

        // Not the fail-closed default: a panel started now has read it before its first response.
        Assert.Equal(SecurityHeaders.ContentSecurityPolicyWithSteamArtwork, await PolicyAfterRestartAsync());

        var entries = (await AuditAsync()).OrderBy(e => e.OccurredAt).ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal((AuditActorType.User, PulsarApiFactory.AdminUserName, "appearance", "settings"),
            (e.ActorType, e.ActorDisplay, e.TargetType, e.TargetId)));
        Assert.Equal(new[] { (false, true), (true, false) }, entries.Select(Change).ToArray());
    }

    private static (bool SteamArtwork, bool Previous) Change(AuditEntry entry)
    {
        using var metadata = JsonDocument.Parse(entry.Metadata);
        return (metadata.RootElement.GetProperty("steamArtwork").GetBoolean(), metadata.RootElement.GetProperty("previousSteamArtwork").GetBoolean());
    }

    private static async Task<AppearanceSettingsResponse> ReadAsync(PanelClient client) =>
        (await client.Http.GetFromJsonAsync<AppearanceSettingsResponse>("/api/v1/appearance", AccessScenario.Json, Ct))!;

    private static async Task<string> PolicyAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        return PolicyOf(response);
    }

    /// <summary>The policy on the first response of another panel started on this database.</summary>
    private async Task<string> PolicyAfterRestartAsync()
    {
        await using var restarted = factory.WithWebHostBuilder(_ => { });
        using var client = restarted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return await PolicyAsync(client);
    }

    private static string PolicyOf(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

    private async Task<List<AuditEntry>> AuditAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Action == AuditActions.Appearance.SettingsChanged).ToListAsync(Ct);
    }
}
