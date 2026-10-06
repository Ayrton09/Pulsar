using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Servers;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Infrastructure.Steam;

namespace Pulsar.Api.Tests.Servers;

/// <summary>Steam's public Workshop API, faked: details for every id, and the items of collections.</summary>
public sealed class FakeSteam : HttpMessageHandler
{
    public Dictionary<string, object> Items { get; } = [];

    public Dictionary<string, string[]> Collections { get; } = [];

    public HttpStatusCode? Failure { get; set; }

    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = await request.Content!.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add($"{request.Method} {request.RequestUri} {form}");
        }

        if (Failure is { } failure)
        {
            return new HttpResponseMessage(failure);
        }

        var ids = form.Split('&').Where(p => p.StartsWith("publishedfileids", StringComparison.Ordinal)).Select(p => p.Split('=')[1]).ToList();
        object body = request.RequestUri!.AbsolutePath switch
        {
            "/ISteamRemoteStorage/GetPublishedFileDetails/v1/" => new
            {
                response = new
                {
                    result = 1,
                    resultcount = ids.Count,
                    publishedfiledetails = ids.Select(id => Items.TryGetValue(id, out var item) ? item : new { publishedfileid = id, result = 9 }),
                },
            },
            "/ISteamRemoteStorage/GetCollectionDetails/v1/" => new
            {
                response = new
                {
                    result = 1,
                    resultcount = ids.Count,
                    collectiondetails = ids.Select(id => Collections.TryGetValue(id, out var children)
                        ? (object)new { publishedfileid = id, result = 1, children = children.Select((c, i) => new { publishedfileid = c, sortorder = children.Length - i, filetype = 0 }) }
                        : new { publishedfileid = id, result = 9 }),
                },
            },
            _ => throw new InvalidOperationException(request.RequestUri.ToString()),
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    public static object Item(string id, string title, long appId, long size = 1024) => new
    {
        publishedfileid = id,
        result = 1,
        consumer_app_id = appId,
        creator_app_id = 766,
        title,
        file_size = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        time_updated = 1790835449,
    };
}

public sealed class WorkshopApiFactory : PulsarApiFactory
{
    public FakeSteam Steam { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(SteamWorkshopClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Steam));
    }
}

/// <summary>Looking up Workshop items for the editor of variables that list them (Project Zomboid's).</summary>
public sealed class WorkshopTests(WorkshopApiFactory factory) : IClassFixture<WorkshopApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ItemsAndCollectionsAreLookedUpOnSteam()
    {
        using var admin = await factory.CreateAdminClientAsync();
        factory.Steam.Items["3806657188"] = FakeSteam.Item("3806657188", "True Water‮\u0007", 108600, 9441774);
        factory.Steam.Items["2690796635"] = FakeSteam.Item("2690796635", "Project Viewpoint Collection", 108600);
        factory.Steam.Items["440"] = FakeSteam.Item("440", "A hat", 440);
        factory.Steam.Collections["2690796635"] = ["3809306528", "3811256204"];

        // The bundled template tells the editor which game's Workshop it is.
        var template = await GameServerTests.TemplateAsync(admin, "project-zomboid");
        Assert.Equal(new TemplateWorkshop(108600, ";"), template.Variables.Single(v => v.Key == "workshopItems").Workshop);

        using var response = await admin.PostAsync("/api/v1/workshop/lookup",
            new WorkshopLookupRequest(108600, ["3806657188", "2690796635", "1", "440", "3806657188"]));
        await AccessScenario.EnsureSuccessAsync(response);
        var items = (await response.Content.ReadFromJsonAsync<List<WorkshopItemView>>(AccessScenario.Json, Ct))!;

        Assert.Equal(["3806657188", "2690796635", "1", "440"], items.Select(i => i.Id)); // asked once each
        Assert.Equal("True Water", items[0].Title); // no direction or control characters from its author
        Assert.Equal(9441774, items[0].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790835449), items[0].UpdatedAt);
        Assert.False(items[0].IsCollection);
        Assert.True(items[1].IsCollection);
        Assert.Equal(["3811256204", "3809306528"], items[1].Items); // in the collection's order
        Assert.False(items[2].Found);
        Assert.True(items[3].OtherGame);
        Assert.All(factory.Steam.Requests, r => Assert.StartsWith("POST https://api.steampowered.com/ISteamRemoteStorage/", r, StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task OnlyWorkshopIdsAreSentAndSteamFailuresAreSaid()
    {
        using var admin = await factory.CreateAdminClientAsync();
        foreach (var ids in new[] { Array.Empty<string>(), ["12a"], ["0123"], ["99999999999999999999999"], [.. Enumerable.Range(1, 101).Select(i => $"{i}")] })
        {
            using var refused = await admin.PostAsync("/api/v1/workshop/lookup", new WorkshopLookupRequest(108600, ids));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        factory.Steam.Failure = HttpStatusCode.ServiceUnavailable;
        try
        {
            using var failed = await admin.PostAsync("/api/v1/workshop/lookup", new WorkshopLookupRequest(108600, ["3806657188"]));
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
            using var problem = JsonDocument.Parse(await failed.Content.ReadAsStringAsync(Ct));
            Assert.Equal("workshop.unavailable", problem.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            factory.Steam.Failure = null;
        }
    }
}
