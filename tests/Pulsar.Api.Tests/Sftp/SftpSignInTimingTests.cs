using System.Diagnostics;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using static Pulsar.Api.Tests.Sftp.SftpTesting;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>A failed password check takes as long whatever failed, so its time does not tell which names are users.</summary>
public sealed class SftpSignInTimingTests(SftpSignInTimingTests.SlowFailuresFactory factory) : IClassFixture<SftpSignInTimingTests.SlowFailuresFactory>
{
    [DatabaseFact]
    public async Task AnUnknownNameAndAWrongPasswordFailAfterTheSameTime()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("timing-user");
        var port = await factory.PortAsync();

        foreach (var (login, password) in new[] { ("timing-user", "not the password"), ("timing-nobody", AccessScenario.Password) })
        {
            var took = Stopwatch.StartNew();
            await FailsAsync(Client(port, login, password));
            Assert.True(took.Elapsed >= SlowFailuresFactory.CheckTime, $"{login}: failed after {took.Elapsed}");
        }
    }

    public sealed class SlowFailuresFactory : SftpApiFactory
    {
        // Far longer than a password hash and a few queries take, so a shorter answer can only be a check that skips it.
        public static readonly TimeSpan CheckTime = TimeSpan.FromSeconds(2);

        protected override void ConfigureSftp(SftpOptions options) => options.FailedPasswordCheckTime = CheckTime;
    }
}
