namespace DbDataKit.State;

/// <summary>
/// Which engine a product's state database is on (plan 13a, P11). SQLite, the default, is a file the product keeps in its own directory and needs nothing else. SQL Server and PostgreSQL are a database the operator
/// names: <see cref="ConnectionString"/> is a connection string whose password may be a secret reference (<c>Password=secret:state-db</c>, or <c>env:</c>), never a literal one the product writes down. A product and a console
/// that share a server each need a database of their own: their tables carry the same names.
/// </summary>
/// <param name="Engine"><c>sqlite</c> (or empty), <c>mssql</c> (also <c>sqlserver</c>) or <c>postgres</c> (also <c>postgresql</c>), in any case.</param>
/// <param name="ConnectionString">The connection string of the server engines; ignored for SQLite.</param>
public sealed record StateSettings(string? Engine = null, string? ConnectionString = null)
{
    /// <summary>The engine id <see cref="StateEngineIds"/> uses for the name given, or a message saying what is accepted.</summary>
    public string EngineId => (Engine ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "sqlite" => StateEngineIds.Sqlite,
        "mssql" or "sqlserver" or "sql-server" => StateEngineIds.MsSql,
        "postgres" or "postgresql" => StateEngineIds.Postgres,
        _ => throw new InvalidOperationException($"The state engine '{Engine}' is not one of sqlite, mssql or postgres."),
    };

    public bool IsSqlite => EngineId == StateEngineIds.Sqlite;
}
