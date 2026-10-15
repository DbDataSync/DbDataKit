using System.Data.Common;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Connections;

/// <summary>Builds an engine connection string from a definition, resolved login and policy. Credentials are applied last and cannot be overridden by properties.</summary>
public interface IConnectionStringComposer
{
    EngineId Engine { get; }
    /// <summary>The full connection string, including credentials. Treat it as a secret.</summary>
    string Compose(ConnectionContext context);
    /// <summary>The same string with credential values masked, for logs and previews.</summary>
    string Describe(ConnectionContext context);
    /// <summary>
    /// Pass-through (Proxy): takes an administrator connection string, removes every integrated-security style key, sets the client's
    /// user and password through the engine's quoting, and asserts that no integrated key remains.
    /// </summary>
    string ApplyPassThroughCredentials(string baseConnectionString, string user, SecretValue password, ConnectionPolicy? policy = null);
}

public enum ReadOnlyEnforcement
{
    /// <summary>Nothing beyond the login can enforce it.</summary>
    None,
    /// <summary>A routing hint only (SQL Server ApplicationIntent=ReadOnly).</summary>
    Routing,
    /// <summary>A session default the session itself can undo (Postgres default_transaction_read_only).</summary>
    SessionDefault,
    /// <summary>The engine refuses writes (DuckDB access_mode=READ_ONLY plus a locked configuration).</summary>
    Engine,
}

/// <summary>A read-only recipe: connection-string keys applied at compose time and statements executed right after open.</summary>
public class ReadOnlySession(
    string engine, ReadOnlyEnforcement enforcement, IReadOnlyList<string> statements,
    IReadOnlyDictionary<string, string>? connectionStringKeys = null, string? notes = null)
{
    public string Engine { get; } = engine;
    public ReadOnlyEnforcement Enforcement { get; } = enforcement;
    /// <summary>Statements run in order right after the connection opens.</summary>
    public IReadOnlyList<string> Statements { get; } = statements;
    /// <summary>Keys the composer adds when the connection is read-only.</summary>
    public IReadOnlyDictionary<string, string> ConnectionStringKeys { get; } = connectionStringKeys ?? new Dictionary<string, string>();
    public string? Notes { get; } = notes;

    public static ReadOnlySession NoneAvailable(string engine, string notes) => new(engine, ReadOnlyEnforcement.None, [], null, notes);

    public virtual async Task ApplyAsync(DbConnection connection, CancellationToken ct = default)
    {
        foreach (var sql in Statements)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed record ConnectionTestResult(bool Success, TimeSpan Elapsed, ConnectionFailure? Failure = null, string? ServerVersion = null);

/// <summary>One engine: how to compose, create, open, test and make read-only a connection.</summary>
public interface IDbProvider
{
    EngineId Engine { get; }
    EngineCaps Caps { get; }
    IConnectionStringComposer Composer { get; }
    ReadOnlySession ReadOnly { get; }
    IFailureMapper FailureMapper { get; }
    ISqlDialect Dialect(ConnectionDefinition definition);
    /// <summary>An unopened connection. Honors read-only keys when the context is read-only.</summary>
    DbConnection Create(ConnectionContext context);
    /// <summary>Creates, opens and applies the read-only recipe when needed. Failures surface as <see cref="ConnectionFailureException"/>.</summary>
    Task<DbConnection> OpenAsync(ConnectionContext context, CancellationToken ct = default);
    Task<ConnectionTestResult> TestAsync(ConnectionContext context, CancellationToken ct = default);
}
