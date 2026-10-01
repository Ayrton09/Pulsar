namespace Pulsar.Infrastructure.Persistence;

/// <summary>Configuration section <c>Database</c>. The connection string itself is <c>ConnectionStrings:Pulsar</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "Pulsar";

    /// <summary>Lowest supported PostgreSQL major version (docs/architecture/02).</summary>
    public const int MinimumPostgresMajorVersion = 15;

    /// <summary>
    /// Apply pending migrations when the panel starts. When false, the panel refuses to start with pending
    /// migrations and they are applied explicitly with <c>Pulsar.Api migrate</c>.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;
}

/// <summary>
/// The panel cannot start because of its database: missing configuration, unreachable server, unsupported version or
/// pending migrations. The message is meant for the administrator and contains no secrets.
/// </summary>
public sealed class DatabaseStartupException : Exception
{
    public DatabaseStartupException(string message)
        : base(message)
    {
    }

    public DatabaseStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DatabaseStartupException()
    {
    }
}
