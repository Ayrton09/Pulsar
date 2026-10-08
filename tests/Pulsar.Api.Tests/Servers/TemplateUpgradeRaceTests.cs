using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// The agent can report a state change of the server while a template upgrade is being saved: the upgrade is saved on
/// the fresh row, not refused as if one of its ports had been taken.
/// </summary>
public sealed class TemplateUpgradeRaceTests(TemplateUpgradeRaceTests.RacingFactory factory) : IClassFixture<TemplateUpgradeRaceTests.RacingFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AnUpgradeSavedWhileTheServerRowChangesIsKept()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "upgrade-race");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        await GameServerTests.AllocateAsync(admin, node.Node.Id, "0.0.0.0", "27015-27020");
        var server = await GameServerTests.InstalledAsync(admin, agent, "css");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(GameServerTests.GamePath("css"), Ct))!.AsObject();
        manifest["version"] = "1.1.0";
        Guid next;
        using (var imported = await admin.PostAsync("/api/v1/templates", new TemplateContentRequest(manifest.ToJsonString())))
        {
            await AccessScenario.EnsureSuccessAsync(imported);
            next = (await imported.Content.ReadFromJsonAsync<TemplateDetails>(AccessScenario.Json, Ct))!.Template.Id;
        }

        factory.Racing.Armed = true;
        using (var upgraded = await admin.PutAsync($"/api/v1/servers/{server}/template", new UpgradeTemplateRequest(next)))
        {
            await AccessScenario.EnsureSuccessAsync(upgraded);
            Assert.Equal("1.1.0", (await upgraded.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.TemplateVersion);
        }

        Assert.False(factory.Racing.Armed); // the race did happen
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "provisioned and installed again");
    }

    /// <summary>The panel, with a change to the server's row slipped in just before an upgrade is saved.</summary>
    public sealed class RacingFactory : PulsarApiFactory
    {
        public RacingInterceptor Racing { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.ConfigureDbContext<PulsarDbContext>(options => options.AddInterceptors(Racing)));
        }
    }

    public sealed class RacingInterceptor : SaveChangesInterceptor
    {
        private int _armed;

        public bool Armed
        {
            get => Volatile.Read(ref _armed) == 1;
            set => Volatile.Write(ref _armed, value ? 1 : 0);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<Server>().FirstOrDefault(e => e.Property(s => s.TemplateId).IsModified) is { } upgrading
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                // As if the agent had just reported a state change of the server.
                await context.Database.ExecuteSqlAsync($"UPDATE servers SET updated_at = updated_at WHERE id = {upgrading.Entity.Id.Value}",
                    cancellationToken);
            }

            return result;
        }
    }
}
