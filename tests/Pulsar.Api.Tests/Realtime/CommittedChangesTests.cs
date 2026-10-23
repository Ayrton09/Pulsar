using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Notifications;
using Pulsar.Application.Realtime;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Realtime;

/// <summary>
/// Browsers and the notification sender hear of changes once they are committed (docs/architecture/07, "Real time"):
/// saves inside a transaction are told when it commits, all of them, never before and never when it rolls back; a save
/// outside one is told as it ends. Whoever is told finds the change when they look.
/// </summary>
public sealed class CommittedChangesTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task SavesInATransactionAreToldOnlyOnceItCommitsAndAreThenSeenFromElsewhere()
    {
        var told = new Recorder();
        using var app = Recording(told);
        var tag = Tag();
        var first = await AddNodeAsync(app, $"first-{tag}");
        var second = await AddNodeAsync(app, $"second-{tag}");
        told.Clear();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await RenameAsync(db, first, $"first-renamed-{tag}");
            await RenameAsync(db, second, $"second-renamed-{tag}");

            Assert.Empty(told.About(first, second));
            Assert.Equal($"first-{tag}", await NameElsewhereAsync(app, first));

            await transaction.CommitAsync(Ct);
        }

        // Both saves, told once committed: what was told is what another context reads at that moment.
        Assert.Equal(
            [(first, $"first-renamed-{tag}"), (second, $"second-renamed-{tag}")],
            told.About(first, second).OrderBy(n => n.Name, StringComparer.Ordinal));
    }

    [DatabaseFact]
    public async Task SavesRolledBackOrNeverCommittedAreNeverTold()
    {
        var told = new Recorder();
        using var app = Recording(told);
        var tag = Tag();
        var node = await AddNodeAsync(app, $"kept-{tag}");
        told.Clear();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await RenameAsync(db, node, $"rolled-back-{tag}");
                await transaction.RollbackAsync(Ct);
            }

            // Disposed without a commit (an early return): rolled back too.
            db.ChangeTracker.Clear();
            await using (await db.Database.BeginTransactionAsync(Ct))
            {
                await RenameAsync(db, node, $"never-committed-{tag}");
            }

            db.ChangeTracker.Clear();
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await transaction.CommitAsync(Ct);
            }
        }

        Assert.Empty(told.About(node));
        Assert.Equal($"kept-{tag}", await NameElsewhereAsync(app, node));

        // Outside a transaction: told as the save ends (its own transaction committed).
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            await RenameAsync(db, node, $"saved-alone-{tag}");
        }

        Assert.Equal([(node, $"saved-alone-{tag}")], told.About(node));
    }

    [DatabaseFact]
    public async Task TheNotificationSenderIsWokenOnlyOnceQueuedNotificationsAreCommitted()
    {
        using var app = Recording(new Recorder());
        var signal = app.Services.GetRequiredService<NotificationSignal>();
        while (await signal.WaitAsync(TimeSpan.Zero, Ct))
        {
            // Whatever starting the panel queued.
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var now = DateTimeOffset.UtcNow;
        var channel = NotificationChannel.Create(NotificationKind.Email, "committed-channel", [NotificationEvent.ServerCrashed], null, "en", true,
            null, ["ops@example.com"], now).Value;
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync(Ct);

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.NotificationDeliveries.Add(Delivery(channel, "server:rolled-back", now));
            await db.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.False(await signal.WaitAsync(TimeSpan.Zero, Ct));

        db.ChangeTracker.Clear();
        NotificationDelivery delivery;
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            delivery = Delivery(channel, "server:committed", now);
            db.NotificationDeliveries.Add(delivery);
            await db.SaveChangesAsync(Ct);

            Assert.False(await signal.WaitAsync(TimeSpan.Zero, Ct));
            await transaction.CommitAsync(Ct);
        }

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, Ct));
        await using var elsewhere = app.Services.CreateAsyncScope();
        Assert.True(await elsewhere.ServiceProvider.GetRequiredService<PulsarDbContext>().NotificationDeliveries
            .AnyAsync(d => d.Id == delivery.Id, Ct));
    }

    private static NotificationDelivery Delivery(NotificationChannel channel, string subject, DateTimeOffset now) =>
        NotificationDelivery.Create(channel, NotificationEvent.ServerCrashed, subject,
            new NotificationMessage("Crashed", "A server crashed.", NotificationSeverity.Error, [], null), now);

    /// <summary>
    /// A panel whose browsers are <paramref name="told"/>, and whose own background work is off (the sender would take
    /// the signal, the node watch could queue notifications of its own).
    /// </summary>
    private WebApplicationFactory<Program> Recording(Recorder told) => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.RemoveAll<IRealtimeNotifier>();
        services.AddSingleton<IRealtimeNotifier>(provider => told.For(provider));
        foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
                     && d.ImplementationType?.Namespace?.StartsWith("Pulsar.", StringComparison.Ordinal) == true).ToList())
        {
            services.Remove(hosted);
        }
    }));

    private static async Task<NodeId> AddNodeAsync(WebApplicationFactory<Program> app, string name)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
        var node = Node.Create(name, null, null, DateTimeOffset.UtcNow).Value;
        db.Nodes.Add(node);
        await db.SaveChangesAsync(Ct);
        return node.Id;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..12];

    private static async Task RenameAsync(PulsarDbContext db, NodeId id, string name)
    {
        var node = await db.Nodes.SingleAsync(n => n.Id == id, Ct);
        Assert.True(node.Update(name, null, null, DateTimeOffset.UtcNow).IsSuccess);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<string?> NameElsewhereAsync(WebApplicationFactory<Program> app, NodeId id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().Nodes.AsNoTracking()
            .Where(n => n.Id == id).Select(n => n.Name).SingleOrDefaultAsync(Ct);
    }

    /// <summary>The nodes browsers were told about, each with its name as another context read it right then.</summary>
    private sealed class Recorder : IRealtimeNotifier
    {
        private readonly ConcurrentQueue<(NodeId Node, string? Name)> _nodes = new();
        private IServiceProvider? _services;

        public Recorder For(IServiceProvider services)
        {
            _services = services;
            return this;
        }

        public List<(NodeId Node, string? Name)> About(params NodeId[] nodes) => [.. _nodes.Where(n => nodes.Contains(n.Node))];

        public void Clear() => _nodes.Clear();

        public void NodesChanged(IReadOnlyCollection<NodeId> nodes)
        {
            using var scope = _services!.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PulsarDbContext>();
            foreach (var node in nodes)
            {
                _nodes.Enqueue((node, db.Nodes.AsNoTracking().Where(n => n.Id == node).Select(n => n.Name).SingleOrDefault()));
            }
        }

        public void ServersChanged(IReadOnlyCollection<ServerId> servers, IReadOnlyDictionary<ServerId, NodeId>? nodes = null)
        {
        }

        public void OperationProgress(ServerId server, Guid operationId, string stage, decimal? percent)
        {
        }

        public void AppearanceChanged()
        {
        }
    }
}
