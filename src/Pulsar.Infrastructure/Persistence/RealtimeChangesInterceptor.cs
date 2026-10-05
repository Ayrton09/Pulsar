using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulsar.Application.Notifications;
using Pulsar.Application.Realtime;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>
/// After every successful save, tells browsers which servers and nodes changed (whoever changed them: a user, an agent's
/// report, a reconciliation), so no code path can forget to; and wakes the notification sender when notifications were queued.
/// </summary>
internal sealed class RealtimeChangesInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, Changes> _pending = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changes = new Changes();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            switch (entry.Entity)
            {
                case Server server:
                    changes.Servers.Add(server.Id);
                    changes.ServerNodes[server.Id] = server.NodeId;
                    break;
                case Operation { ServerId: { } serverId }:
                    changes.Servers.Add(serverId);
                    break;
                case ServerVariable variable:
                    changes.Servers.Add(variable.ServerId);
                    break;
                case Backup backup:
                    changes.Servers.Add(backup.ServerId);
                    break;
                case ScheduledTask task:
                    changes.Servers.Add(task.ServerId);
                    break;
                case ScheduledTaskRun run:
                    changes.Servers.Add(run.ServerId);
                    break;
                case NodeAllocation allocation:
                    changes.Nodes.Add(allocation.NodeId);
                    if (allocation.ServerId is { } holder)
                    {
                        changes.Servers.Add(holder);
                    }

                    break;
                case Node node:
                    changes.Nodes.Add(node.Id);
                    break;
                case NodeCredential credential:
                    changes.Nodes.Add(credential.NodeId);
                    break;
                case NotificationDelivery when entry.State == EntityState.Added:
                    changes.NotificationsQueued = true;
                    break;
            }
        }

        _pending.AddOrUpdate(context, changes);
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var changes))
        {
            return;
        }

        _pending.Remove(context);
        if (changes.NotificationsQueued)
        {
            // Committed: the sender finds them now.
            (services.GetService(typeof(NotificationSignal)) as NotificationSignal)?.Notify();
        }

        // Resolved late: the infrastructure also runs without the application layer (startup checks, tests).
        if (services.GetService(typeof(IRealtimeNotifier)) is not IRealtimeNotifier notifier)
        {
            return;
        }

        if (changes.Servers.Count > 0)
        {
            notifier.ServersChanged(changes.Servers, changes.ServerNodes);
        }

        if (changes.Nodes.Count > 0)
        {
            notifier.NodesChanged(changes.Nodes);
        }
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }

    private sealed class Changes
    {
        public HashSet<ServerId> Servers { get; } = [];

        public Dictionary<ServerId, NodeId> ServerNodes { get; } = [];

        public HashSet<NodeId> Nodes { get; } = [];

        public bool NotificationsQueued { get; set; }
    }
}
