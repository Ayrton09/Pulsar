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

    /// <summary>Answers every request instead of the fake's own answers.</summary>
    public Func<HttpResponseMessage>? Answer { get; set; }

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

        if (Answer is { } answer)
        {
            return answer();
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

    [DatabaseFact]
    public async Task SteamsAnswerIsReadWithinItsLimitsAndRedirectsAreNotFollowed()
    {
        using var admin = await factory.CreateAdminClientAsync();
        factory.Steam.Items["3806657188"] = FakeSteam.Item("3806657188", new string('x', 300), 108600);
        using (var response = await admin.PostAsync("/api/v1/workshop/lookup", new WorkshopLookupRequest(108600, ["3806657188"])))
        {
            await AccessScenario.EnsureSuccessAsync(response);
            var items = (await response.Content.ReadFromJsonAsync<List<WorkshopItemView>>(AccessScenario.Json, Ct))!;
            Assert.Equal(new string('x', 128), items[0].Title);
        }

        // An endless answer without a length: read up to the cap, not buffered whole first.
        var endless = new List<EndlessStream>();
        factory.Steam.Answer = () =>
        {
            var stream = new EndlessStream();
            lock (endless)
            {
                endless.Add(stream);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        try
        {
            await AssertUnavailableAsync(admin);
            Assert.NotEmpty(endless);
            Assert.All(endless, s => Assert.True(s.BytesRead < 16 * 1024 * 1024, $"{s.BytesRead} bytes read"));

            factory.Steam.Answer = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/details") } };
            factory.Steam.Requests.Clear();
            await AssertUnavailableAsync(admin);
            Assert.All(factory.Steam.Requests, r => Assert.StartsWith("POST https://api.steampowered.com/", r, StringComparison.Ordinal));
        }
        finally
        {
            factory.Steam.Answer = null;
        }
    }

    [DatabaseFact]
    public async Task AnAnswerThatStallsEndsAtTheClientsTimeout()
    {
        // The client's timeout (15 s in production) covers reading the body too: one second here.
        (await factory.CreateAdminClientAsync()).Dispose();
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(SteamWorkshopClient.ClientName).ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(1))));
        using var admin = await PanelClient.CreateAsync(app);
        using (var login = await admin.LoginAsync(PulsarApiFactory.AdminUserName, PulsarApiFactory.AdminPassword))
        {
            await AccessScenario.EnsureSuccessAsync(login);
        }

        await admin.GetSessionAsync();
        factory.Steam.Answer = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await AssertUnavailableAsync(admin).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            factory.Steam.Answer = null;
        }
    }

    private static async Task AssertUnavailableAsync(PanelClient admin)
    {
        using var failed = await admin.PostAsync("/api/v1/workshop/lookup", new WorkshopLookupRequest(108600, ["3806657188"]));
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        using var problem = JsonDocument.Parse(await failed.Content.ReadAsStringAsync(Ct));
        Assert.Equal("workshop.unavailable", problem.RootElement.GetProperty("code").GetString());
    }

    /// <summary>A body that sends a few bytes, then nothing more until the reader gives up.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read asynchronously.");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                "{\"res"u8.CopyTo(buffer.Span);
                return 5;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that never ends (up to 256 MiB) and has no length; counts what was read of it.</summary>
    private sealed class EndlessStream : Stream
    {
        private const long End = 256L * 1024 * 1024;
        private long _read;

        public long BytesRead => Interlocked.Read(ref _read);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, End - _read);
            buffer.AsSpan(offset, n).Fill((byte)' ');
            Interlocked.Add(ref _read, n);
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
