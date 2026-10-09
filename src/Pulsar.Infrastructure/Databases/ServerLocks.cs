using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Databases;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Infrastructure.Databases;

internal sealed class ServerLocks(PulsarDbContext db) : IServerLocks
{
    public async Task LockAsync(ServerId server, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A server is locked inside a transaction.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM servers WHERE id = {server.Value} FOR UPDATE", cancellationToken);
    }
}
