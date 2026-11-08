using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;

namespace Pulsar.Application.Servers;

/// <summary>
/// Whether a transfer of the server runs (1.0.16): while it does, only its own operations change the server. A file, a
/// mod or a restore changed on the old node after the transfer's backup would go with that node's copy, and one on the
/// new node would land in the middle of its install and restore.
/// </summary>
internal static class TransferGate
{
    public static Task<bool> RunningAsync(IPulsarDbContext db, ServerId id, CancellationToken cancellationToken) =>
        db.ServerSequences.AnyAsync(s => s.ServerId == id && s.Origin == SequenceOrigin.Transfer && s.Status == SequenceStatus.Running,
            cancellationToken);
}
