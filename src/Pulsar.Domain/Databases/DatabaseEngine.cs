namespace Pulsar.Domain.Databases;

/// <summary>What a <see cref="DatabaseHost"/> runs: fixed when the host is added (its databases are of that kind).</summary>
public enum DatabaseEngine
{
    /// <summary>MySQL or MariaDB (Phase 13).</summary>
    Mysql,

    /// <summary>PostgreSQL (1.0.37): a database owned by a role of the same name that may connect to it alone.</summary>
    Postgres,
}

public static class DatabaseEngines
{
    /// <summary>The port its servers listen on unless told otherwise.</summary>
    public static int DefaultPort(this DatabaseEngine engine) => engine == DatabaseEngine.Postgres ? 5432 : 3306;

    /// <summary>How manifests name it (<c>database.engine</c>): <c>mysql</c> or <c>postgres</c>.</summary>
    public static string Key(this DatabaseEngine engine) => engine == DatabaseEngine.Postgres ? "postgres" : "mysql";
}
