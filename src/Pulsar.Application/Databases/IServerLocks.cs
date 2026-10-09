using Pulsar.Domain.Common;

namespace Pulsar.Application.Databases;

/// <summary>Row locks on servers (PostgreSQL <c>FOR UPDATE</c>; Infrastructure).</summary>
public interface IServerLocks
{
    /// <summary>Locks the server's row until the current transaction ends: what is checked against it stays true until then.</summary>
    Task LockAsync(ServerId server, CancellationToken cancellationToken);
}
