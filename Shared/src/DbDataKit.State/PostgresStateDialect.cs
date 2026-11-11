using System.Data.Common;

namespace DbDataKit.State;

/// <summary>
/// PostgreSQL as the state store.
/// <para>
/// The closest of the three to SQLite for this purpose: <c>ON CONFLICT</c> is Postgres's own syntax
/// that SQLite adopted, and <c>LIMIT</c> is shared. What differs is identity DDL and the schema
/// version having to live in a table.
/// </para>
/// <para>
/// Phase 109g moved this off a hard <c>Npgsql</c> package reference and onto the library layer — see
/// <see cref="MsSqlStateDialect"/>'s doc comment, which explains the same change there in full.
/// </para>
/// </summary>
public sealed class PostgresStateDialect : StateDialect
{

    // 40P01 deadlock detected and 40001 serialization failure: the transaction was rolled back whole, so the action can run again
    public override bool ShouldRetry(Exception exception) =>
        exception is System.Data.Common.DbException { SqlState: "40P01" or "40001" };

    /// <summary>The library catalog id this dialect resolves its connection
    /// through.</summary>
    public const string LibraryId = "npgsql";

    private readonly Func<string, DbProviderFactory> _factories;

    public PostgresStateDialect(Func<string, DbProviderFactory> factories) => _factories = factories;

    public override string Engine => StateEngineIds.Postgres;

    public override DbConnection CreateConnection(string connectionString)
    {
        var connection = _factories(LibraryId).CreateConnection()
            ?? throw new InvalidOperationException(
                $"The '{LibraryId}' library's factory did not produce a connection.");
        connection.ConnectionString = connectionString;
        return connection;
    }

    public override string ParameterName(string name) => $"@{name}";

    public override string Limit(string parameterName) => $"LIMIT {Parameter(parameterName)}";

    /// <summary>The partial-index predicate is written into the conflict clause, which is how both
    /// Postgres and SQLite are told *which* index to infer when a table has more than one.</summary>
    public override string InsertOrIgnore(
        string table, string columns, string values, string? conflictTarget, string? conflictWhere = null) =>
        $"INSERT INTO {table} ({columns}) VALUES ({values}) " +
        $"ON CONFLICT{(conflictTarget is null ? "" : $" ({conflictTarget})")}" +
        $"{(conflictWhere is null ? "" : $" WHERE {conflictWhere}")} DO NOTHING;";

    public override string Upsert(
        string table, string columns, string values, string conflictTarget, string updates) =>
        $"INSERT INTO {table} AS existing ({columns}) VALUES ({values}) " +
        $"ON CONFLICT ({conflictTarget}) DO UPDATE SET {updates.Replace("EXISTING.", "existing.")};";

    /// <summary><c>GENERATED ALWAYS</c> rather than <c>SERIAL</c>: the standard spelling, and the one
    /// that refuses an explicit value rather than quietly accepting one and desynchronising the
    /// sequence. Nothing here ever supplies its own id.</summary>
    public override string IdentityKey(string column) =>
        $"{column} BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY";

    public override string Text => "TEXT";

    // Postgres indexes TEXT without a length, so there is nothing to bound — see StateDialect.Text
    // for the engine that cannot.
    public override string KeyText => "TEXT";

    public override string Integer => "BIGINT";

    public override string Uuid => "UUID";

    public override string UuidAsText(string column) => $"CAST({column} AS TEXT)";

    public override string Time => "TIMESTAMPTZ";

    public override string Json => "JSONB";

    public override string Bool => "BOOLEAN";

    public override string Decimal => "NUMERIC(38,10)";

    public override string Blob => "BYTEA";

    /// <summary>The <c>vector</c> feature is the <c>pgvector</c> extension: switched on in the database by the migration that needs it (a login that cannot is refused before any DDL by the feature check).</summary>
    public override string EnableFeature(string feature) => feature == "vector" ? "CREATE EXTENSION IF NOT EXISTS vector" : base.EnableFeature(feature);

    public override int GetSchemaVersion(DbConnection connection)
    {
        using var ensure = connection.CreateCommand();
        ensure.CommandText = "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INT NOT NULL);";
        ensure.ExecuteNonQuery();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1;";
        return read.ExecuteScalar() is { } value and not DBNull ? Convert.ToInt32(value) : 0;
    }

    public override void SetSchemaVersion(DbConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE SchemaVersion SET Version = @version;
            INSERT INTO SchemaVersion (Version)
            SELECT @version WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);
            """;
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@version";
        parameter.Value = version;
        cmd.Parameters.Add(parameter);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Only when the schema is missing: <c>CREATE SCHEMA IF NOT EXISTS</c> asks for the database-level right even when the schema is there, which would make every upgrade need it.</summary>
    public override string? CreateSchema(string schema) =>
        $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = '{schema}') THEN CREATE SCHEMA {schema}; END IF; END $$;";

    public override bool TableExists(DbConnection connection, string qualifiedName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@name) IS NOT NULL;";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = qualifiedName;
        cmd.Parameters.Add(parameter);
        return Convert.ToBoolean(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A session-level advisory lock on a constant key: released when the connection closes even if the process dies.</summary>
    public override IDisposable? AcquireMigrationLock(DbConnection connection)
    {
        Run(connection, "SELECT pg_advisory_lock(7270034281);");
        return new Release(connection);
    }

    public override string? FindLegacySchemaVersion(DbConnection connection)
    {
        if (!TableExists(connection, "schemaversion")) return null;
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1;";
        return $"the table SchemaVersion holds version {read.ExecuteScalar()}";
    }

    private static void Run(DbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class Release(DbConnection connection) : IDisposable
    {
        public void Dispose() => Run(connection, "SELECT pg_advisory_unlock(7270034281);");
    }
}
