using System.Net;
using Pulsar.Api.Auth;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Access;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Access;

/// <summary>
/// Caps on what users add (API keys, scheduled tasks, backup storages, Steam accounts) hold when the last place is asked
/// for many times at once: one request gets it, the others are refused.
/// </summary>
public sealed class ConcurrentLimitTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const int Racing = 8;

    [DatabaseFact]
    public async Task AUsersLastApiKeyGoesToOneRequest()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await RaceForTheLastAsync(admin, "/api/v1/account/api-keys", ApiKey.MaxPerUser, i => new CreateApiKeyRequest($"Key {i}", null, null));
    }

    [DatabaseFact]
    public async Task AServersLastScheduledTaskGoesToOneRequest()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "limits");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        var server = (await ServerApi.CreateAsync(admin, node.Node.Id, "Limits")).Server.Id;
        await RaceForTheLastAsync(admin, $"/api/v1/servers/{server}/schedules", ScheduledTask.MaxTasksPerServer,
            i => new ScheduledTaskRequest($"Restart {i}", "0 4 * * *", "UTC", ScheduledAction.Restart, null, false, false, true));
    }

    [DatabaseFact]
    public async Task ThePanelsLastBackupStorageGoesToOneRequest()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await RaceForTheLastAsync(admin, "/api/v1/backup-storages", BackupStorage.MaxStorages,
            i => new BackupStorageRequest($"Storage {i}", "https://s3.example.com", "auto", "pulsar-backups", null, "AKIDEXAMPLE", "secret", true));
    }

    [DatabaseFact]
    public async Task ThePanelsLastSteamAccountGoesToOneRequest()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await RaceForTheLastAsync(admin, "/api/v1/steam-accounts", SteamAccount.MaxAccounts, i => new SteamAccountRequest($"Account {i}", $"steamuser{i}", "apassword1"));
    }

    /// <summary>Fills all places but one, then asks for the last one <see cref="Racing"/> times at once.</summary>
    private static async Task RaceForTheLastAsync(PanelClient admin, string path, int limit, Func<int, object> request)
    {
        for (var i = 0; i < limit - 1; i++)
        {
            using var response = await admin.PostAsync(path, request(i));
            await AccessScenario.EnsureSuccessAsync(response);
        }

        var answers = await Task.WhenAll(Enumerable.Range(limit, Racing).Select(async i =>
        {
            using var response = await admin.PostAsync(path, request(i));
            return response.StatusCode;
        }));
        Assert.Single(answers, s => s == HttpStatusCode.Created);
        Assert.Equal(Racing - 1, answers.Count(s => s == HttpStatusCode.Conflict));
    }
}
