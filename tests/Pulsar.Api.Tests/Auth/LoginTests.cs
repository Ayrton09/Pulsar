using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Auth;

public sealed class LoginTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const string Password = "another long passphrase";

    [DatabaseFact]
    public async Task LoginMeAndLogout()
    {
        using var admin = await factory.CreateAdminClientAsync();

        using (var me = await admin.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            var user = await me.Content.ReadFromJsonAsync<CurrentUserResponse>(TestContext.Current.CancellationToken);
            Assert.Equal(PulsarApiFactory.AdminUserName, user!.UserName);
        }

        using (var logout = await admin.PostAsync("/api/v1/auth/logout"))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using var afterLogout = await admin.GetAsync("/api/v1/auth/me");
        await SetupTests.AssertProblemAsync(afterLogout, HttpStatusCode.Unauthorized, "auth.required");
    }

    [DatabaseFact]
    public async Task WrongPasswordAndUnknownUserGetTheSameAnswerAndAreAudited()
    {
        using var admin = await factory.CreateAdminClientAsync();
        using var client = await PanelClient.CreateAsync(factory);

        using (var wrongPassword = await client.LoginAsync(PulsarApiFactory.AdminUserName, "not the password at all"))
        {
            await SetupTests.AssertProblemAsync(wrongPassword, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        using (var unknown = await client.LoginAsync("nobody-here", "not the password at all"))
        {
            await SetupTests.AssertProblemAsync(unknown, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var failures = await db.AuditLog.Where(e => e.Action == AuditActions.Auth.LoginFailed).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(failures, e => e.Metadata.Contains("nobody-here", StringComparison.Ordinal) && e.TargetId is null);
        Assert.Contains(failures, e => e.TargetType == "user");
        Assert.All(failures, e => Assert.DoesNotContain("not the password", e.Metadata, StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task DisabledUsersCannotSignIn()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await CreateUserAsync("disabled-user", user => user.IsDisabled = true);
        using var client = await PanelClient.CreateAsync(factory);

        using var response = await client.LoginAsync("disabled-user", Password);

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [DatabaseFact]
    public async Task AccountIsLockedAfterFiveFailuresEvenForTheRightPassword()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await CreateUserAsync("locked-user");
        using var client = await PanelClient.CreateAsync(factory);

        for (var i = 0; i < 5; i++)
        {
            using var failure = await client.LoginAsync("locked-user", "wrong password number " + i);
        }

        using var response = await client.LoginAsync("locked-user", Password);

        await SetupTests.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [DatabaseFact]
    public async Task UsersWithoutRolesSignInWithNoPermissions()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await CreateUserAsync("plain-user");
        using var client = await PanelClient.CreateAsync(factory);

        using var response = await client.LoginAsync("plain-user", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(TestContext.Current.CancellationToken);
        Assert.False(login!.TwoFactorRequired);
        Assert.Empty(login.User!.Permissions);
    }

    [DatabaseFact]
    public async Task LoginIsRateLimitedPerClient()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Auth:PermitLimit", "2"));
        using var admin = await factory.CreateAdminClientAsync();
        using var client = await PanelClient.CreateAsync(limited);

        for (var i = 0; i < 2; i++)
        {
            using var allowed = await client.LoginAsync("someone", "whatever password");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var rejected = await client.LoginAsync("someone", "whatever password");
        await SetupTests.AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests, "rate_limit.exceeded");
    }

    private async Task CreateUserAsync(string userName, Action<PulsarUser>? configure = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PulsarUser>>();
        var user = new PulsarUser { Id = UserId.New(), UserName = userName, CreatedAt = DateTimeOffset.UtcNow };
        configure?.Invoke(user);
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));
    }
}
