using Pulsar.Application.Databases;
using Pulsar.Domain.Databases;

namespace Pulsar.Infrastructure.Databases;

/// <summary>The client for what each host runs (<see cref="DatabaseHostConnection.Engine"/>).</summary>
internal sealed class DatabaseHostClient(MySqlDatabaseHostClient mysql, PostgresDatabaseHostClient postgres) : IDatabaseHostClient
{
    public Task<string> CheckAsync(DatabaseHostConnection host, CancellationToken cancellationToken) => For(host).CheckAsync(host, cancellationToken);

    public Task CreateAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken) =>
        For(host).CreateAsync(host, name, userHost, password, cancellationToken);

    public Task DropAsync(DatabaseHostConnection host, string name, string userHost, CancellationToken cancellationToken) =>
        For(host).DropAsync(host, name, userHost, cancellationToken);

    public Task SetPasswordAsync(DatabaseHostConnection host, string name, string userHost, string password, CancellationToken cancellationToken) =>
        For(host).SetPasswordAsync(host, name, userHost, password, cancellationToken);

    private IDatabaseHostClient For(DatabaseHostConnection host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Engine switch
        {
            DatabaseEngine.Mysql => mysql,
            DatabaseEngine.Postgres => postgres,
            _ => throw new ArgumentException("Not an engine Pulsar knows.", nameof(host)),
        };
    }
}
