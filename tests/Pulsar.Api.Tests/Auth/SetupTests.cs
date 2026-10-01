using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Auth;

public sealed class SetupTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private const string SetupPath = "/api/v1/auth/setup";

    [DatabaseFact]
    public async Task FirstAdministratorIsCreatedOnceAndOnlyWithTheSetupToken()
    {
        using var client = await PanelClient.CreateAsync(factory);
        var session = await client.GetSessionAsync();
        Assert.True(session.SetupRequired);
        Assert.Null(session.User);

        var token = factory.Services.GetRequiredService<InitialSetup>().IssueToken();

        using (var wrongToken = await client.PostAsync(SetupPath, Request("not-the-token")))
        {
            await AssertProblemAsync(wrongToken, HttpStatusCode.BadRequest, "setup.invalid_token");
        }

        using (var weakPassword = await client.PostAsync(SetupPath, Request(token) with { Password = "short" }))
        {
            await AssertProblemAsync(weakPassword, HttpStatusCode.BadRequest, "validation.failed");
        }

        using (var created = await client.PostAsync(SetupPath, Request(token)))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var user = await created.Content.ReadFromJsonAsync<CurrentUserResponse>(TestContext.Current.CancellationToken);
            Assert.Equal("owner", user!.UserName);
            Assert.Equal(PermissionCatalog.AllKeys.Order(StringComparer.Ordinal), user.Permissions);
        }

        // The setup signs the administrator in, and the panel no longer asks for setup.
        session = await client.GetSessionAsync();
        Assert.False(session.SetupRequired);
        Assert.Equal("owner", session.User?.UserName);

        using (var again = await client.PostAsync(SetupPath, Request(token)))
        {
            await AssertProblemAsync(again, HttpStatusCode.Conflict, "setup.completed");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var outcomes = await db.AuditLog.Where(e => e.Action == AuditActions.Auth.Setup)
            .OrderBy(e => e.OccurredAt).Select(e => e.Outcome).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([AuditOutcome.Failure, AuditOutcome.Success], outcomes);
    }

    [DatabaseFact]
    public async Task StateChangingRequestsWithoutTheAntiforgeryHeaderAreRejected()
    {
        using var client = await PanelClient.CreateAsync(factory, sendAntiforgeryHeader: false);

        using var response = await client.PostAsync(SetupPath, Request("whatever"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "antiforgery.invalid");
    }

    private static SetupRequest Request(string token) =>
        new(token, "owner", "a long enough passphrase", "Owner", "owner@example.com");

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }
}
