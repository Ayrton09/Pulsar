using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Pulsar.Api.Tests.Infrastructure;
using static Pulsar.Api.Tests.Sftp.SftpTesting;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// A failed password check takes as long whatever failed, in the browser and over SFTP, so its time does not tell which
/// names are users.
/// </summary>
public sealed class SftpSignInTimingTests(SftpSignInTimingTests.SlowFailuresFactory factory) : IClassFixture<SftpSignInTimingTests.SlowFailuresFactory>
{
    private static readonly (string Login, string Password)[] s_failures = [("timing-user", "not the password"), ("timing-nobody", AccessScenario.Password)];

    [DatabaseFact]
    public async Task AnUnknownNameAndAWrongPasswordFailAfterTheSameTimeOverSftp()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("timing-user");
        var port = await factory.PortAsync();

        foreach (var (login, password) in s_failures)
        {
            var took = Stopwatch.StartNew();
            await FailsAsync(Client(port, login, password));
            Assert.True(took.Elapsed >= SlowFailuresFactory.CheckTime, $"{login}: failed after {took.Elapsed}");
        }
    }

    [DatabaseFact]
    public async Task AnUnknownNameAndAWrongPasswordFailAfterTheSameTimeInTheBrowser()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("timing-web-user");

        foreach (var (login, password) in s_failures)
        {
            using var client = await PanelClient.CreateAsync(factory);
            var took = Stopwatch.StartNew();
            using var response = await client.LoginAsync(login.Replace("timing-", "timing-web-", StringComparison.Ordinal), password);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(took.Elapsed >= SlowFailuresFactory.CheckTime, $"{login}: failed after {took.Elapsed}");
        }
    }

    [DatabaseFact]
    public async Task FailedChecksOnOneNameDoNotWaitOutEachOthersTime()
    {
        // Wrong passwords sent for someone's name (from many addresses) must not make the owner's sign-in queue behind
        // each one's wait: it is waited out without the name's turn.
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("timing-queue-user");
        using var first = await PanelClient.CreateAsync(factory);
        using var second = await PanelClient.CreateAsync(factory);

        var took = Stopwatch.StartNew();
        await Task.WhenAll(FailAsync(first), FailAsync(second));

        Assert.True(took.Elapsed < SlowFailuresFactory.CheckTime * 1.75, $"two failures took {took.Elapsed}");

        static async Task FailAsync(PanelClient client)
        {
            using var response = await client.LoginAsync("timing-queue-user", "not the password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    public sealed class SlowFailuresFactory : SftpApiFactory
    {
        // Far longer than a password hash and a few queries take, so a shorter answer can only be a check that skips it.
        public static readonly TimeSpan CheckTime = TimeSpan.FromSeconds(2);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimiting:Auth:FailedPasswordCheckTime", CheckTime.ToString());
        }
    }
}
