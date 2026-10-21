using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Realtime;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Access;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Tests.Auth;

/// <summary>
/// The browsers' live channel follows the session (docs/architecture/08): what ends a user's sessions or changes what
/// they may see closes their open connections, and a connection only opens for a session that is still valid.
/// </summary>
public sealed class LiveSessionTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task DisablingAUserClosesTheirLiveChannel()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var created = await scenario.CreateUserAsync("live-disabled", await scenario.CreateRoleAsync("live-disabled-role", Permissions.Server.View));
        using var user = await scenario.SignInAsync("live-disabled");
        await using var hub = await BrowserHub.ConnectAsync(factory, user);
        Assert.True(await hub.Connection.InvokeAsync<bool>("WatchServers", Ct));
        var closed = ClosedAsync(hub.Connection);

        using (var disable = await admin.PutAsync($"/api/v1/users/{created.Id}", new UpdateUserRequest(null, null, IsDisabled: true)))
        {
            await AccessScenario.EnsureSuccessAsync(disable);
        }

        await closed.WaitAsync(s_closeTimeout, Ct);
        await AssertCannotStayConnectedAsync(user);
    }

    [DatabaseFact]
    public async Task APasswordChangedElsewhereClosesThisLiveChannelForGood()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("live-password");
        using var stolen = await scenario.SignInAsync("live-password");
        using var owner = await scenario.SignInAsync("live-password");
        await using var hub = await BrowserHub.ConnectAsync(factory, stolen);
        var closed = ClosedAsync(hub.Connection);

        using (var changed = await owner.PostAsync("/api/v1/auth/password", new ChangePasswordRequest(AccessScenario.Password, "the owner's new password")))
        {
            await AccessScenario.EnsureSuccessAsync(changed);
        }

        await closed.WaitAsync(s_closeTimeout, Ct);
        // The stolen cookie still passes for up to a minute (security stamp interval); the live channel checks it now.
        await AssertCannotStayConnectedAsync(stolen);
        await using var mine = await BrowserHub.ConnectAsync(factory, owner);
        Assert.Equal(HubConnectionState.Connected, mine.Connection.State);
    }

    [DatabaseFact]
    public async Task SigningOutClosesTheLiveChannelsOfTheOtherTabs()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("live-logout", await scenario.CreateRoleAsync("live-logout-role", Permissions.Server.View));
        using var user = await scenario.SignInAsync("live-logout");
        // Another tab of the same browser: same cookie, its own connection.
        await using var otherTab = await BrowserHub.ConnectAsync(factory, user);
        Assert.True(await otherTab.Connection.InvokeAsync<bool>("WatchServers", Ct));
        var closed = ClosedAsync(otherTab.Connection);

        using (var logout = await user.PostAsync("/api/v1/auth/logout"))
        {
            await AccessScenario.EnsureSuccessAsync(logout);
        }

        // The browser dropped the cookie, so the tab's reconnection is refused and its session reloads.
        await closed.WaitAsync(s_closeTimeout, Ct);
    }

    [DatabaseFact]
    public async Task ChangingARoleClosesLiveChannelsSoWatchesAreCheckedAgain()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var role = await scenario.CreateRoleAsync("live-role", Permissions.Server.View);
        await scenario.CreateUserAsync("live-role-user", role);
        using var user = await scenario.SignInAsync("live-role-user");
        await using var hub = await BrowserHub.ConnectAsync(factory, user);
        Assert.True(await hub.Connection.InvokeAsync<bool>("WatchServers", Ct));
        var closed = ClosedAsync(hub.Connection);

        using (var update = await admin.PutAsync($"/api/v1/roles/{role.Id}", new UpdateRoleRequest("live-role", null, [Permissions.Node.View])))
        {
            await AccessScenario.EnsureSuccessAsync(update);
        }

        await closed.WaitAsync(s_closeTimeout, Ct);
        await using var again = await BrowserHub.ConnectAsync(factory, user);
        Assert.False(await again.Connection.InvokeAsync<bool>("WatchServers", Ct));
    }

    internal static Task ClosedAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        return closed.Task;
    }

    // Refused outright, or opened and closed by the panel at once.
    private async Task AssertCannotStayConnectedAsync(PanelClient client)
    {
        BrowserHub hub;
        try
        {
            hub = await BrowserHub.ConnectAsync(factory, client);
        }
        catch (Exception refused) when (BrowserHub.IsRefusal(refused))
        {
            return;
        }

        await using (hub)
        {
            var closed = ClosedAsync(hub.Connection);
            if (hub.Connection.State != HubConnectionState.Disconnected)
            {
                await closed.WaitAsync(s_closeTimeout, Ct);
            }
        }
    }
}

/// <summary>A panel that runs <see cref="AfterStampCheck"/> once, right after the next check of a session's security stamp.</summary>
public sealed class LateSessionEndApiFactory : PulsarApiFactory
{
    private Action<PulsarUser>? _afterStampCheck;

    public Action<PulsarUser>? AfterStampCheck
    {
        get => Volatile.Read(ref _afterStampCheck);
        set => Volatile.Write(ref _afterStampCheck, value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<SignInManager<PulsarUser>>(
            provider => ActivatorUtilities.CreateInstance<HookedSignInManager>(provider, this))));
    }

    private sealed class HookedSignInManager(
        LateSessionEndApiFactory factory,
        UserManager<PulsarUser> userManager,
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<PulsarUser> claimsFactory,
        IOptions<IdentityOptions> optionsAccessor,
        ILogger<SignInManager<PulsarUser>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<PulsarUser> confirmation)
        : SignInManager<PulsarUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        public override async Task<PulsarUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
        {
            var user = await base.ValidateSecurityStampAsync(principal);
            if (user is not null && Interlocked.Exchange(ref factory._afterStampCheck, null) is { } hook)
            {
                hook(user);
            }

            return user;
        }
    }
}

public sealed class LiveSessionRaceTests(LateSessionEndApiFactory factory) : IClassFixture<LateSessionEndApiFactory>
{
    [DatabaseFact]
    public async Task SessionsEndedWhileTheChannelOpensCloseItToo()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await scenario.CreateUserAsync("live-race");
        using var user = await scenario.SignInAsync("live-race");
        var connections = factory.Services.GetRequiredService<BrowserConnections>();

        // The user's sessions end (disabled, a password changed elsewhere) right after the channel found theirs valid.
        factory.AfterStampCheck = checkedUser => connections.EndUser(checkedUser.Id);
        BrowserHub hub;
        try
        {
            hub = await BrowserHub.ConnectAsync(factory, user);
        }
        catch (Exception refused) when (BrowserHub.IsRefusal(refused))
        {
            Assert.Null(factory.AfterStampCheck);
            return;
        }

        await using (hub)
        {
            // The handshake is answered before the connection's check runs: closed once it ran and ended the session.
            var closed = LiveSessionTests.ClosedAsync(hub.Connection);
            if (hub.Connection.State != HubConnectionState.Disconnected)
            {
                await closed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            Assert.Null(factory.AfterStampCheck);
        }
    }
}
