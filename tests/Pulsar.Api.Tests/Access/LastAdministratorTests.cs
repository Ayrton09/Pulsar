using System.Net;
using System.Net.Http.Json;
using Pulsar.Api.Access;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;

namespace Pulsar.Api.Tests.Access;

/// <summary>
/// Some enabled user always holds the Administrator role (a panel of its own: every administrator here is this test's).
/// </summary>
public sealed class LastAdministratorTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    [DatabaseFact]
    public async Task TwoAdministratorsCannotDisableEachOther()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var first = scenario.Admin;
        var roles = await first.Http.GetFromJsonAsync<RoleSummary[]>(new Uri("/api/v1/roles", UriKind.Relative), AccessScenario.Json,
            TestContext.Current.CancellationToken);
        var second = await scenario.CreateUserAsync("second-admin", roles!.Single(r => r.IsSystem));
        using var secondSession = await scenario.SignInAsync("second-admin");
        var firstId = (await first.GetSessionAsync()).User!.Id;

        using (var disabled = await first.PutAsync($"/api/v1/users/{second.Id}", new UpdateUserRequest(null, null, IsDisabled: true)))
        {
            await AccessScenario.EnsureSuccessAsync(disabled);
        }

        // The second one's session has not been checked again yet (within a minute), or both asked at the same moment:
        // the first one stays, the only enabled administrator.
        using (var back = await secondSession.PutAsync($"/api/v1/users/{firstId}", new UpdateUserRequest(null, null, IsDisabled: true)))
        {
            Assert.NotEqual(HttpStatusCode.OK, back.StatusCode);
        }

        using var me = await first.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }
}
