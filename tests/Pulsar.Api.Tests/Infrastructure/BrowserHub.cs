using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Realtime;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// The browser's live channel (/hubs/panel) as a test sees it: connected with a signed-in client's cookies, recording
/// every push it gets.
/// </summary>
public sealed class BrowserHub : IAsyncDisposable
{
    private readonly List<ConsoleEntry> _console = [];
    private readonly List<Guid> _serversChanged = [];
    private readonly List<ServerMetricsView> _metrics = [];
    private readonly List<Guid> _nodesChanged = [];
    private readonly List<NodeMetricsUpdate> _nodeMetrics = [];

    private BrowserHub(HubConnection connection)
    {
        Connection = connection;
        connection.On<ConsoleOutputView>(nameof(IPanelClient.ConsoleOutput), output =>
        {
            lock (_console)
            {
                _console.AddRange(output.Entries);
            }
        });
        connection.On<IReadOnlyCollection<Guid>>(nameof(IPanelClient.ServersChanged), servers =>
        {
            lock (_serversChanged)
            {
                _serversChanged.AddRange(servers);
            }
        });
        connection.On<ServerMetricsView>(nameof(IPanelClient.ServerMetrics), metrics =>
        {
            lock (_metrics)
            {
                _metrics.Add(metrics);
            }
        });
        connection.On<IReadOnlyCollection<Guid>>(nameof(IPanelClient.NodesChanged), nodes =>
        {
            lock (_nodesChanged)
            {
                _nodesChanged.AddRange(nodes);
            }
        });
        connection.On<NodeMetricsUpdate>(nameof(IPanelClient.NodeMetrics), update =>
        {
            lock (_nodeMetrics)
            {
                _nodeMetrics.Add(update);
            }
        });
    }

    public HubConnection Connection { get; }

    /// <summary>Console entries pushed so far, in arrival order.</summary>
    public IReadOnlyList<ConsoleEntry> Console
    {
        get
        {
            lock (_console)
            {
                return [.. _console];
            }
        }
    }

    public IReadOnlyList<Guid> ServersChanged
    {
        get
        {
            lock (_serversChanged)
            {
                return [.. _serversChanged];
            }
        }
    }

    public IReadOnlyList<ServerMetricsView> Metrics
    {
        get
        {
            lock (_metrics)
            {
                return [.. _metrics];
            }
        }
    }

    public IReadOnlyList<Guid> NodesChanged
    {
        get
        {
            lock (_nodesChanged)
            {
                return [.. _nodesChanged];
            }
        }
    }

    public IReadOnlyList<NodeMetricsUpdate> NodeMetrics
    {
        get
        {
            lock (_nodeMetrics)
            {
                return [.. _nodeMetrics];
            }
        }
    }

    /// <param name="origin">The page's origin, as a browser sends it; the panel's own by default.</param>
    public static async Task<BrowserHub> ConnectAsync(WebApplicationFactory<Program> factory, PanelClient client, string origin = "https://localhost")
    {
        var cookies = client.CookieHeader;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri("https://localhost/hubs/panel"), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var socket = factory.Server.CreateWebSocketClient();
                    socket.ConfigureRequest = request =>
                    {
                        request.Headers.Cookie = cookies;
                        request.Headers.Origin = origin;
                    };
                    return await socket.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(options => ProtocolJson.Configure(options.PayloadSerializerOptions))
            .Build();
        var hub = new BrowserHub(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return hub;
    }

    /// <summary>The console text the browser has: snapshot entries and pushed ones, each sequence once, in order.</summary>
    public static string Text(IEnumerable<ConsoleEntry> entries) =>
        string.Concat(entries.DistinctBy(e => e.Sequence).OrderBy(e => e.Sequence).Select(e => e.IsGap ? "[gap]" : e.Text));

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    /// <summary>For tests that expect the connection to be refused.</summary>
    public static bool IsRefusal(Exception exception) => exception is WebSocketException or HttpRequestException or InvalidOperationException;
}
