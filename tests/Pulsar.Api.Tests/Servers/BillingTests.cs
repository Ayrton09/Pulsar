using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// What a billing system does with an API key (Pulsar 1.0.24, docs/guides/billing.md): a customer and a server under
/// its own ids, found again by them, the server given to the customer, suspended, lifted and deleted.
/// </summary>
public sealed class BillingTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ABillingSystemRunsACustomersServerByItsOwnIds()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "billing");
        var node = agent.Config!.NodeId;
        await GameServerTests.AllocateAsync(admin, node, "0.0.0.0", "27240-27259");
        var css = (await GameServerTests.TemplateAsync(admin, "css")).Template.Id;
        using var billing = await KeyAsync(admin);

        // The customer, under the billing system's id; asked for again (a retry), it is refused, and found by that id.
        UserResponse customer;
        using (var created = await billing.PostAsJsonAsync("/api/v1/users",
                   new CreateUserRequest("billing-customer", AccessScenario.Password, null, null, null, "whmcs:client-42"), AccessScenario.Json, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            customer = (await created.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!;
            Assert.Equal("whmcs:client-42", customer.ExternalId);
        }

        await ProblemAsync(await billing.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("billing-customer-2", AccessScenario.Password, null, null, null, "whmcs:client-42"), AccessScenario.Json, Ct),
            HttpStatusCode.Conflict, AccessErrors.UserExternalIdTaken.Code);
        await ProblemAsync(await billing.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("billing-customer-3", AccessScenario.Password, null, null, null, "has spaces"), AccessScenario.Json, Ct),
            HttpStatusCode.BadRequest, ExternalIds.Invalid.Code);
        var found = await billing.GetFromJsonAsync<List<UserResponse>>("/api/v1/users?externalId=whmcs:client-42", AccessScenario.Json, Ct);
        Assert.Equal(customer.Id, Assert.Single(found!).Id);

        // Its server, under the service's id, with the plan's caps, given to the customer.
        ServerDetails server;
        using (var created = await billing.PostAsJsonAsync("/api/v1/servers",
                   new CreateServerRequest(node, css, "Customer 42", null, ExternalId: "whmcs:service-7"), AccessScenario.Json, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            server = (await created.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!;
            Assert.Equal("whmcs:service-7", server.Server.ExternalId);
        }

        var id = server.Server.Id;
        await ServerApi.WaitForAsync(admin, id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        await ProblemAsync(await billing.PostAsJsonAsync("/api/v1/servers",
            new CreateServerRequest(node, css, "Again", null, ExternalId: "whmcs:service-7"), AccessScenario.Json, Ct),
            HttpStatusCode.Conflict, ServerErrors.ExternalIdTaken.Code);
        using (var limits = await billing.PutAsJsonAsync($"/api/v1/servers/{id}/limits", new ServerLimitsRequest(100, 2048), AccessScenario.Json, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, limits.StatusCode);
        }

        using (var given = await billing.PutAsJsonAsync($"/api/v1/servers/{id}/owner", new ServerOwnerRequest(customer.Id), AccessScenario.Json, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, given.StatusCode);
            Assert.Equal(customer.Id, (await given.Content.ReadFromJsonAsync<ServerDetails>(AccessScenario.Json, Ct))!.Server.OwnerId);
        }

        var services = await billing.GetFromJsonAsync<List<ServerSummary>>("/api/v1/servers?externalId=whmcs:service-7", AccessScenario.Json, Ct);
        Assert.Equal(id, Assert.Single(services!).Id);

        // The customer runs it as theirs.
        using var client = await scenario.SignInAsync("billing-customer");
        await ServerApi.CommandAsync(client, id, "start");
        await ServerApi.WaitForAsync(client, id, s => s.Server.RuntimeState == ServerRuntimeState.Running, "running");
        // The billing system's id of it is for whoever hosts it, not for the customer, who cannot find it out by trying.
        Assert.Null((await ServerApi.GetAsync(client, id))!.Server.ExternalId);
        Assert.Empty((await client.Http.GetFromJsonAsync<List<ServerSummary>>(new Uri("/api/v1/servers?externalId=whmcs:service-7", UriKind.Relative),
            AccessScenario.Json, Ct))!);

        // Unpaid, then paid: suspended and lifted; then cancelled.
        using (var suspended = await billing.PostAsJsonAsync($"/api/v1/servers/{id}/suspend", new SuspendServerRequest("Overdue invoice"), AccessScenario.Json, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        }

        await ProblemAsync(await client.PostAsync($"/api/v1/servers/{id}/start"), HttpStatusCode.Conflict, ServerErrors.Suspended.Code);
        await ServerApi.WaitForAsync(admin, id, s => s.Server.RuntimeState == ServerRuntimeState.Offline, "stopped");
        using (var lifted = await billing.PostAsync(new Uri($"/api/v1/servers/{id}/unsuspend", UriKind.Relative), null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        }

        using (var deleted = await billing.DeleteAsync(new Uri($"/api/v1/servers/{id}", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        // The ids change hands: the customer's moved to another account of theirs.
        using (var moved = await billing.PutAsJsonAsync($"/api/v1/users/{customer.Id}/external-id", new ExternalIdRequest(null), AccessScenario.Json, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
            Assert.Null((await moved.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!.ExternalId);
        }
    }

    [DatabaseFact]
    public async Task AServerIsGivenOnlyByWhoeverHoldsEverythingItsOwnerWould()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        await using var agent = await TemplateUpdateTests.ConnectedAgentAsync(factory, admin, "billing-owner");
        await GameServerTests.AllocateAsync(admin, agent.Config!.NodeId, "0.0.0.0", "27260-27269");
        var id = await GameServerTests.InstalledAsync(admin, agent, "css");
        var customer = await scenario.CreateUserAsync("billing-given");

        // A user manager who may not run the server cannot hand it out.
        using var manager = await scenario.SignedInWithAsync("billing-user-manager", Permissions.User.View, Permissions.User.Manage, Permissions.Server.View);
        await ProblemAsync(await manager.PutAsync($"/api/v1/servers/{id}/owner", new ServerOwnerRequest(customer.Id)), HttpStatusCode.Forbidden,
            ServerErrors.OwnerForbidden.Code);

        // Given and taken back: the customer sees it, then does not.
        using var client = await scenario.SignInAsync("billing-given");
        using (var given = await admin.PutAsync($"/api/v1/servers/{id}/owner", new ServerOwnerRequest(customer.Id)))
        {
            await AccessScenario.EnsureSuccessAsync(given);
        }

        Assert.NotNull(await ServerApi.GetAsync(client, id));
        using (var taken = await admin.PutAsync($"/api/v1/servers/{id}/owner", new ServerOwnerRequest(null)))
        {
            await AccessScenario.EnsureSuccessAsync(taken);
        }

        using var after = await client.GetAsync($"/api/v1/servers/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    /// <summary>The billing system's key: the administrator's, as such modules are set up.</summary>
    private async Task<HttpClient> KeyAsync(PanelClient admin)
    {
        using var response = await admin.PostAsync("/api/v1/account/api-keys", new CreateApiKeyRequest("Billing", null, null));
        await AccessScenario.EnsureSuccessAsync(response);
        var key = (await response.Content.ReadFromJsonAsync<CreatedApiKey>(AccessScenario.Json, Ct))!;
        var client = factory.CreateDefaultClient(new Uri("https://localhost"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);
        return client;
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            if (code is not null)
            {
                using var problem = JsonDocument.Parse(body);
                Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
            }
        }
    }
}
