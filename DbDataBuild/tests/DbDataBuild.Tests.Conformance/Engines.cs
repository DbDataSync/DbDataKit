using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataBuild.Tests.Conformance;

/// <summary>An engine under test: creates an ephemeral database, runs scripts exactly as given with bound parameters, and reads rows back.</summary>
public abstract class Engine : IProbeEngine
{
    public abstract string Name { get; }          // sqlserver | postgres (the target names)
    public virtual string Label => Name;
    public virtual int? Version => null;
    public abstract string ColumnType(string logical);
    public abstract string QuoteIdent(string name);
    protected abstract DbConnection Connect(string? database);
    protected abstract string CreateDatabaseSql(string name);
    protected abstract string DropDatabaseSql(string name);
    protected virtual string? AfterCreateDatabaseSql(string name) => null;

    private DbConnection? connection;
    private string? database;

    /// <summary>Only loopback engines may be used: the suite creates and drops databases.</summary>
    protected static void RequireLoopback(string host)
    {
        var allowed = new[] { "127.0.0.1", "localhost", "::1", "[::1]" };
        if (!allowed.Contains(host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to run conformance tests against `{host}`: only local throwaway engines are allowed.");
    }

    public async Task StartAsync()
    {
        database = "ddb_conf_" + Guid.NewGuid().ToString("N")[..10];
        using (var admin = Connect(null))
        {
            await admin.OpenAsync();
            await ExecAsync(admin, CreateDatabaseSql(database));
            if (AfterCreateDatabaseSql(database) is { } more) await ExecAsync(admin, more);
        }
        connection = Connect(database);
        await connection.OpenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (connection != null) await connection.DisposeAsync();
        if (database != null)
        {
            using var admin = Connect(null);
            await admin.OpenAsync();
            try { await ExecAsync(admin, DropDatabaseSql(database)); } catch { /* a leftover throwaway database is harmless */ }
        }
    }

    /// <summary>A connection string for the ephemeral database, for tools under test that open their own connections.</summary>
    public string ConnectionString => ConnectionStringFor(database ?? throw new InvalidOperationException("Engine not started."));
    protected abstract string ConnectionStringFor(string database);

    public DbConnection Conn => connection ?? throw new InvalidOperationException("Engine not started.");

    protected static async Task ExecAsync(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;           // the scale tests fill tables with millions of rows
        await cmd.ExecuteNonQueryAsync();
    }

    public Task ExecAsync(string sql) => ExecAsync(Conn, sql);

    public async Task CreateProbeTableAsync(IReadOnlyList<ProbeRow> rows)
    {
        var sqlServer = Name == "sqlserver";
        await ExecAsync($"CREATE TABLE probe (id INT NOT NULL, i INT, j INT, d {(sqlServer ? "FLOAT" : "DOUBLE PRECISION")}, n DECIMAL(10, 2), s {(sqlServer ? "NVARCHAR(50) COLLATE Latin1_General_100_BIN2" : "VARCHAR(50) COLLATE \"C\"")}, dt DATE, ts {(sqlServer ? "DATETIME2(6)" : "TIMESTAMP(6)")})");
        string Str(string? v) => v == null ? "NULL" : (sqlServer ? "N'" : "'") + v.Replace("'", "''") + "'";
        string Dt(string? v) => v == null ? "NULL" : $"CAST('{v}' AS DATE)";
        string Ts(string? v) => v == null ? "NULL" : $"CAST('{v}' AS {(sqlServer ? "DATETIME2(6)" : "TIMESTAMP")})";
        await ExecAsync("INSERT INTO probe VALUES " + string.Join(", ", rows.Select(r => $"({r.Id}, {r.I}, {r.J}, {r.D}, {r.N}, {Str(r.S)}, {Dt(r.Dt)}, {Ts(r.Ts)})")));
    }

    public async Task<ProbeRows> QueryAsync(string sql)
    {
        try
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = await cmd.ExecuteReaderAsync();
            var types = Enumerable.Range(0, r.FieldCount).Select(i => r.GetDataTypeName(i)).ToList();
            var rows = new List<object?[]>();
            while (await r.ReadAsync()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray());
            return new(rows, types);
        }
        catch (DbException ex) { throw new EngineQueryException(ex.GetType().Name + ": " + ex.Message.Split('\n')[0].Trim()); }
    }

    /// <summary>Runs committed script text unmodified; only parameters are bound, through the driver.</summary>
    public async Task RunScriptAsync(string script, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = script;
        foreach (var (name, value) in parameters ?? new Dictionary<string, object?>()) cmd.Parameters.Add(Param(cmd, name, value));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a resolver and returns its single value (null for SQL NULL).</summary>
    public async Task<object?> ScalarAsync(string sql)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    private static DbParameter Param(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = "@" + name;
        if (value is Typed typed)
        {
            p.DbType = typed.Type;
            p.Value = typed.Value ?? DBNull.Value;
            return p;
        }
        p.Value = value ?? DBNull.Value;
        p.DbType = value switch { DateTime => DbType.DateTime2, DateOnly => DbType.Date, long => DbType.Int64, int => DbType.Int32, decimal => DbType.Decimal, _ => DbType.String };
        if (value is DateOnly d) p.Value = d.ToDateTime(TimeOnly.MinValue);
        return p;
    }

    /// <summary>All rows of a query as normalized strings, sorted, so engines and the oracle compare equal.</summary>
    public async Task<List<string>> RowsAsync(string sql)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await rd.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, rd.FieldCount).Select(i => Normalize.Value(rd.IsDBNull(i) ? null : rd.GetValue(i), rd.GetDataTypeName(i)))));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    public async Task<bool> TableExistsAsync(string likeName) => (await RowsAsync(TempTableProbe(likeName))).Count > 0;

    protected abstract string TempTableProbe(string name);
}

/// <summary>A parameter value with an explicit type, so that a NULL still binds as the column's type (PostgreSQL will not cast text to numeric).</summary>
public sealed record Typed(DbType Type, object? Value);

/// <param name="variable">The environment variable holding the connection (SQL Server 2022, or the 2025 container).</param>
/// <param name="compatibilityLevel">The database compatibility level to set (160 is SQL Server 2022, 170 is 2025); null leaves the server's default.</param>
/// <param name="label">What the run is called when it is not the plain `sqlserver`.</param>
/// <param name="version">What `targets.sqlserver.version` would say for this run: 16 for T-SQL of SQL Server 2022 (or 2025 at level 160), 17 for 2025 at level 170.</param>
public sealed class SqlServerEngine(string variable = EngineEnv.SqlServer, int? compatibilityLevel = null, string label = "sqlserver", int? version = null) : Engine
{
    private readonly SqlConnectionStringBuilder csb = new(EngineEnv.Get(variable) ?? "");
    private readonly bool checkedLoopback = Check(EngineEnv.Get(variable) ?? "");

    private static bool Check(string connection) { RequireLoopback(new SqlConnectionStringBuilder(connection).DataSource.Split(',')[0].Replace("tcp:", "")); return true; }

    public override string Name => "sqlserver";
    public override string Label => label;
    public override int? Version => version;
    protected override string? AfterCreateDatabaseSql(string name) => compatibilityLevel is { } level ? $"ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = {level}" : null;
    public override string QuoteIdent(string name) => "[" + name + "]";
    public override string ColumnType(string logical) => logical switch
    {
        "TIMESTAMP" => "DATETIME2(6)", "BIGINT" => "BIGINT", "DATE" => "DATE", var t when t.StartsWith("DECIMAL") => t, var t when t.StartsWith("VARCHAR") => "N" + t, _ => logical,
    };

    protected override DbConnection Connect(string? database)
    {
        var b = new SqlConnectionStringBuilder(csb.ConnectionString) { InitialCatalog = database ?? "master", TrustServerCertificate = true };
        return new SqlConnection(b.ConnectionString);
    }

    protected override string ConnectionStringFor(string database) =>
        new SqlConnectionStringBuilder(csb.ConnectionString) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;

    protected override string CreateDatabaseSql(string name) => $"CREATE DATABASE [{name}]";
    protected override string DropDatabaseSql(string name) => $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
    protected override string TempTableProbe(string name) => $"SELECT 1 FROM tempdb.sys.tables WHERE name LIKE '{name}%'";
}

public sealed class PostgresEngine : Engine
{
    private readonly NpgsqlConnectionStringBuilder csb = new(EngineEnv.Get(EngineEnv.Postgres) ?? "");

    public PostgresEngine() => RequireLoopback(csb.Host ?? "");

    public override string Name => "postgres";
    public override string QuoteIdent(string name) => "\"" + name + "\"";
    public override string ColumnType(string logical) => logical switch { "TIMESTAMP" => "TIMESTAMP(6)", var t when t.StartsWith("DECIMAL") => t.Replace("DECIMAL", "NUMERIC"), _ => logical };

    protected override DbConnection Connect(string? database)
    {
        var b = new NpgsqlConnectionStringBuilder(csb.ConnectionString) { Database = database ?? "postgres", Pooling = false };
        return new NpgsqlConnection(b.ConnectionString);
    }

    protected override string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(csb.ConnectionString) { Database = database, Pooling = false }.ConnectionString;

    protected override string CreateDatabaseSql(string name) => $"CREATE DATABASE \"{name}\"";
    protected override string DropDatabaseSql(string name) => $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
    protected override string TempTableProbe(string name) => $"SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname LIKE 'pg_temp%' AND c.relname LIKE '{name}%'";
}

public static class Normalize
{
    public static string Value(object? v, string typeName) => v switch
    {
        null => "∅",
        DateTime dt when typeName.Equals("date", StringComparison.OrdinalIgnoreCase) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => Math.Round(m, 9).ToString("0.#########", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("0.#########", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
