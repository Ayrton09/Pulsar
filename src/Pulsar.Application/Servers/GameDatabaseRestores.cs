using System.Collections.Concurrent;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Servers;

/// <summary>
/// Restores waiting for their node to send the game's database back (1.0.37), by operation: one that ends without it (its
/// archive refused, its node gone) no longer holds a place among its node's transfers.
/// </summary>
public sealed class GameDatabaseRestores
{
    private readonly ConcurrentDictionary<OperationId, FileTransfers.Download> _waiting = new();

    internal void Add(OperationId operation, FileTransfers.Download download) => _waiting[operation] = download;

    internal void Remove(OperationId operation) => _waiting.TryRemove(operation, out _);

    /// <summary>The restore ended: a dump it never sent is no longer waited for.</summary>
    public void Ended(OperationId operation)
    {
        if (_waiting.TryRemove(operation, out var download))
        {
            download.Abort();
        }
    }
}
