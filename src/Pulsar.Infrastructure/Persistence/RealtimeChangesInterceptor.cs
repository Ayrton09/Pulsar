using System.Data.Common;
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
/// Once changes are committed, tells browsers which servers and nodes changed (whoever changed them: a user, an agent's
/// report, a reconciliation), so no code path can forget to; and wakes the notification sender when notifications were queued.
/// A save outside a transaction of the caller's is committed when it ends; the saves made in one (each a savepoint) are
/// gathered and told when it commits, and forgotten when it rolls back or is disposed without a commit: a browser asking
/// again, or the sender looking for notifications, must find what it was told about.
/// </summary>
internal sealed class RealtimeChangesInterceptor(IServiceProvider services) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    // The save under way, and what the saves of the context's open transaction gathered, by context. A transaction is
    // told apart by EF's id for it: the provider reuses its own transaction object for the connection's next one.
    private readonly ConditionalWeakTable<DbContext, Changes> _pending = [];
    private readonly ConditionalWeakTable<DbContext, Uncommitted> _uncommitted = [];

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
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (Take(eventData) is { } changes)
        {
            Publish(changes);
        }
    }

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionCommitted(transaction, eventData);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Take(eventData);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Take(eventData);
        return Task.CompletedTask;
    }

    // A commit that failed is not known to have happened: nothing is told (browsers fetch again at their next change).
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Take(eventData);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Take(eventData);
        return Task.CompletedTask;
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
                case ServerSequence sequence:
                    changes.Servers.Add(sequence.ServerId);
                    break;
                case GameUpdateWatch watch:
                    changes.Servers.Add(watch.ServerId);
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

    private void Saved(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var changes))
        {
            return;
        }

        _pending.Remove(context);
        // Inside the caller's transaction: kept for its commit. Otherwise the save's own transaction committed already.
        if (context.Database.CurrentTransaction is { } transaction)
        {
            lock (_uncommitted)
            {
                // What an earlier transaction gathered is dropped: it was disposed without a commit (rolled back).
                if (!_uncommitted.TryGetValue(context, out var open) || open.TransactionId != transaction.TransactionId)
                {
                    open = new Uncommitted(transaction.TransactionId, new Changes());
                    _uncommitted.AddOrUpdate(context, open);
                }

                open.Changes.Add(changes);
            }

            return;
        }

        Publish(changes);
    }

    /// <summary>What the saves of the transaction that ended gathered, forgotten here.</summary>
    private Changes? Take(TransactionEventData eventData)
    {
        if (eventData.Context is not { } context)
        {
            return null;
        }

        lock (_uncommitted)
        {
            if (!_uncommitted.TryGetValue(context, out var open) || open.TransactionId != eventData.TransactionId)
            {
                return null;
            }

            _uncommitted.Remove(context);
            return open.Changes;
        }
    }

    private void Publish(Changes changes)
    {
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

    private sealed record Uncommitted(Guid TransactionId, Changes Changes);

    private sealed class Changes
    {
        public HashSet<ServerId> Servers { get; } = [];

        public Dictionary<ServerId, NodeId> ServerNodes { get; } = [];

        public HashSet<NodeId> Nodes { get; } = [];

        public bool NotificationsQueued { get; set; }

        public void Add(Changes other)
        {
            Servers.UnionWith(other.Servers);
            foreach (var (server, node) in other.ServerNodes)
            {
                ServerNodes[server] = node;
            }

            Nodes.UnionWith(other.Nodes);
            NotificationsQueued |= other.NotificationsQueued;
        }
    }
}
