using System.Data.Common;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.Drivers.Apache.Spark;
using System.Net.Http.Json;
using Oracle.ManagedDataAccess.Client;

namespace DbDataBuild.Tests.Conformance;

/// <summary>`Key=Value;Key=Value`, the shape the test environment variables use for the engines that have no connection string of their own.</summary>
internal static class Settings
{
    public static Dictionary<string, string> Parse(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1].Trim() : "", StringComparer.OrdinalIgnoreCase);
}

/// <summary>Oracle Database 23ai Free (Oracle's own container image). Probed only: the table is created in the SYSTEM schema of the pluggable database and dropped at the end.</summary>
public sealed class OracleProbeEngine : IProbeEngine
{
    private readonly Dictionary<string, string> settings;
    private OracleConnection? connection;

    public OracleProbeEngine(string setting)
    {
        settings = Settings.Parse(setting);
        Loopback.Require(settings["Host"]);
    }

    public string Name => "oracle";
    public bool EmptyStringIsNull => true;

    public async Task StartAsync()
    {
        connection = new OracleConnection($"User Id={settings["User Id"]};Password={settings["Password"]};Data Source={settings["Host"]}:{settings["Port"]}/{settings["Service"]};Pooling=false;Connection Timeout=60");
        await connection.OpenAsync();
        await ExecAsync("BEGIN EXECUTE IMMEDIATE 'DROP TABLE probe'; EXCEPTION WHEN OTHERS THEN NULL; END;");
    }

    private async Task ExecAsync(string sql)
    {
        using var cmd = connection!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task CreateProbeTableAsync(IReadOnlyList<ProbeRow> rows)
    {
        await ExecAsync("CREATE TABLE probe (id NUMBER(10) NOT NULL, i NUMBER(10), j NUMBER(10), d BINARY_DOUBLE, n NUMBER(10, 2), s VARCHAR2(50 CHAR), dt DATE, ts TIMESTAMP(6))");
        string Str(string? v) => v == null ? "NULL" : "'" + v.Replace("'", "''") + "'";
        string Dt(string? v) => v == null ? "NULL" : $"TO_DATE('{v}', 'YYYY-MM-DD')";
        string Ts(string? v) => v == null ? "NULL" : $"TO_TIMESTAMP('{v}', 'YYYY-MM-DD HH24:MI:SS')";
        foreach (var r in rows) await ExecAsync($"INSERT INTO probe VALUES ({r.Id}, {r.I}, {r.J}, {r.D}, {r.N}, {Str(r.S)}, {Dt(r.Dt)}, {Ts(r.Ts)})");
    }

    public async Task<ProbeRows> QueryAsync(string sql)
    {
        try
        {
            using var cmd = connection!.CreateCommand();
            cmd.CommandText = sql.Trim().TrimEnd(';');
            using var r = await cmd.ExecuteReaderAsync();
            var types = Enumerable.Range(0, r.FieldCount).Select(i => r.GetDataTypeName(i)).ToList();
            var rows = new List<object?[]>();
            while (await r.ReadAsync())
                rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : Read(r, i)).ToArray());
            return new(rows, types);
        }
        catch (DbException ex) { throw new EngineQueryException(ex.GetType().Name + ": " + ex.Message.Split('\n')[0].Trim()); }
    }

    // a NUMBER wider than a .NET decimal (or any value the driver cannot convert) is read in Oracle's own form, as text
    private static object Read(DbDataReader r, int i)
    {
        try { return r.GetValue(i); }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException) { return ((OracleDataReader)r).GetOracleValue(i).ToString() ?? ""; }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection == null) return;
        try { await ExecAsync("BEGIN EXECUTE IMMEDIATE 'DROP TABLE probe'; EXCEPTION WHEN OTHERS THEN NULL; END;"); } catch (DbException) { }
        await connection.DisposeAsync();
    }
}

/// <summary>
/// The Google BigQuery emulator (goccy/bigquery-emulator, a community project: it implements GoogleSQL with ZetaSQLite, not BigQuery itself), spoken to over BigQuery's REST API with `jobs.query`. The client library
/// is not used: it cannot read the `+Inf` the emulator writes for a float, and the probes want the values as the service sends them.
/// </summary>
public sealed class BigQueryProbeEngine : IProbeEngine
{
    private const string Project = "ddb";
    private const string Dataset = "ddb";
    private readonly Dictionary<string, string> settings;
    private HttpClient? http;

    public BigQueryProbeEngine(string setting)
    {
        settings = Settings.Parse(setting);
        Loopback.Require(settings["Host"]);
    }

    public string Name => "bigquery";

    public async Task StartAsync()
    {
        http = new HttpClient { BaseAddress = new Uri($"http://{settings["Host"]}:{settings["Port"]}/bigquery/v2/"), Timeout = TimeSpan.FromSeconds(60) };
        // the dataset may exist from an earlier run
        using var created = await http.PostAsJsonAsync($"projects/{Project}/datasets", new { datasetReference = new { projectId = Project, datasetId = Dataset } });
        await QueryAsync("DROP TABLE IF EXISTS `ddb.ddb.probe`");
    }

    public async Task CreateProbeTableAsync(IReadOnlyList<ProbeRow> rows)
    {
        await QueryAsync("CREATE TABLE `ddb.ddb.probe` (id INT64 NOT NULL, i INT64, j INT64, d FLOAT64, n NUMERIC(10, 2), s STRING, dt DATE, ts DATETIME)");
        string Str(string? v) => v == null ? "NULL" : "'" + v.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        string Dt(string? v) => v == null ? "NULL" : $"DATE '{v}'";
        string Ts(string? v) => v == null ? "NULL" : $"DATETIME '{v}'";
        foreach (var r in rows) await QueryAsync($"INSERT INTO `ddb.ddb.probe` VALUES ({r.Id}, {r.I}, {r.J}, {r.D}, {r.N}, {Str(r.S)}, {Dt(r.Dt)}, {Ts(r.Ts)})");
    }

    public async Task<ProbeRows> QueryAsync(string sql)
    {
        // the emulator does not resolve a bare table name through the default dataset the way BigQuery does, so the probe table is named in full
        sql = System.Text.RegularExpressions.Regex.Replace(sql, @"\b(FROM|JOIN)\s+probe\b", "$1 `ddb.ddb.probe`");
        try
        {
            using var response = await http!.PostAsJsonAsync($"projects/{Project}/queries", new { query = sql.Trim().TrimEnd(';'), useLegacySql = false, defaultDataset = new { projectId = Project, datasetId = Dataset }, timeoutMs = 30000 });
            var body = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
                throw new EngineQueryException("BigQueryError: " + (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? body : body).Split('\n')[0].Trim());
            var root = doc.RootElement;
            var fields = root.TryGetProperty("schema", out var schema) && schema.TryGetProperty("fields", out var f) ? f.EnumerateArray().ToList() : [];
            var types = fields.Select(x => x.GetProperty("type").GetString() ?? "").ToList();
            var rows = new List<object?[]>();
            if (root.TryGetProperty("rows", out var raw))
                foreach (var row in raw.EnumerateArray())
                    rows.Add(row.GetProperty("f").EnumerateArray().Select((cell, i) => Convert(cell.GetProperty("v"), types[i])).ToArray());
            return new(rows, types);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new EngineQueryException(ex.GetType().Name + ": " + ex.Message.Split('\n')[0].Trim());
        }
    }

    // the service sends every value as text; a missing value is JSON null
    private static object? Convert(System.Text.Json.JsonElement v, string type)
    {
        if (v.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
        var text = v.GetString()!;
        return type switch
        {
            "INTEGER" or "INT64" => long.Parse(text, CultureInfo.InvariantCulture),
            "FLOAT" or "FLOAT64" => text switch { "+Inf" or "Infinity" => double.PositiveInfinity, "-Inf" or "-Infinity" => double.NegativeInfinity, "NaN" => double.NaN, _ => double.Parse(text, CultureInfo.InvariantCulture) },
            "NUMERIC" => decimal.Parse(text, CultureInfo.InvariantCulture),
            "BOOLEAN" or "BOOL" => bool.Parse(text),
            "DATE" => DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "DATETIME" => DateTime.Parse(text, CultureInfo.InvariantCulture),
            "TIMESTAMP" => DateTimeOffset.FromUnixTimeMilliseconds((long)(double.Parse(text, CultureInfo.InvariantCulture) * 1000)).UtcDateTime,
            _ => text,
        };
    }

    public ValueTask DisposeAsync()
    {
        http?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Apache Spark SQL through its Thrift server (HiveServer2), read with the ADBC Spark driver. Spark 4 in its default ANSI mode.</summary>
public sealed class SparkProbeEngine : IProbeEngine
{
    private readonly Dictionary<string, string> settings;
    private AdbcDatabase? database;
    private AdbcConnection? connection;

    public SparkProbeEngine(string setting)
    {
        settings = Settings.Parse(setting);
        Loopback.Require(settings["Host"]);
    }

    public string Name => "spark";

    public async Task StartAsync()
    {
        database = new SparkDriver().Open(new Dictionary<string, string>
        {
            [SparkParameters.HostName] = settings["Host"],
            [SparkParameters.Port] = settings["Port"],
            [SparkParameters.Type] = SparkServerTypeConstants.Http,
            [SparkParameters.Path] = "/cliservice",
            ["adbc.http_options.tls.enabled"] = "false",
            [SparkParameters.AuthType] = SparkAuthTypeConstants.Basic,
            [AdbcOptions.Username] = "ddb",
            [AdbcOptions.Password] = "ddb",
        });
        connection = database.Connect(null);
        await RunAsync("DROP TABLE IF EXISTS probe");
    }

    private async Task RunAsync(string sql)
    {
        using var stmt = connection!.CreateStatement();
        stmt.SqlQuery = sql;
        var result = await stmt.ExecuteQueryAsync();
        using var stream = result.Stream;
        if (stream != null) while (await stream.ReadNextRecordBatchAsync() != null) { }
    }

    public async Task CreateProbeTableAsync(IReadOnlyList<ProbeRow> rows)
    {
        await RunAsync("CREATE TABLE probe (id INT NOT NULL, i INT, j INT, d DOUBLE, n DECIMAL(10, 2), s STRING, dt DATE, ts TIMESTAMP_NTZ) USING parquet");
        string Str(string? v) => v == null ? "NULL" : "'" + v.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        string Dt(string? v) => v == null ? "CAST(NULL AS DATE)" : $"DATE '{v}'";
        string Ts(string? v) => v == null ? "CAST(NULL AS TIMESTAMP_NTZ)" : $"TIMESTAMP_NTZ '{v}'";
        await RunAsync("INSERT INTO probe VALUES " + string.Join(", ", rows.Select(r => $"({r.Id}, {(r.I == "NULL" ? "CAST(NULL AS INT)" : r.I)}, {r.J}, {(r.D == "NULL" ? "CAST(NULL AS DOUBLE)" : r.D)}, {(r.N == "NULL" ? "CAST(NULL AS DECIMAL(10, 2))" : r.N)}, {Str(r.S)}, {Dt(r.Dt)}, {Ts(r.Ts)})")));
    }

    public async Task<ProbeRows> QueryAsync(string sql)
    {
        try
        {
            using var stmt = connection!.CreateStatement();
            stmt.SqlQuery = sql.Trim().TrimEnd(';');
            var result = await stmt.ExecuteQueryAsync();
            using var stream = result.Stream!;
            var types = stream.Schema.FieldsList.Select(f => f.DataType.Name is "date32" or "date64" ? "date" : f.DataType.Name).ToList();
            var rows = new List<object?[]>();
            while (await stream.ReadNextRecordBatchAsync() is { } batch)
            {
                for (var row = 0; row < batch.Length; row++)
                    rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => ArrowValue(batch.Column(c), row)).ToArray());
                batch.Dispose();
            }
            return new(rows, types);
        }
        catch (Exception ex) when (ex is AdbcException or InvalidOperationException or NotSupportedException)
        {
            throw new EngineQueryException(ex.GetType().Name + ": " + ex.Message.Split('\n')[0].Trim());
        }
    }

    private static object? ArrowValue(IArrowArray array, int row)
    {
        if (array.IsNull(row)) return null;
        return array switch
        {
            StringArray a => a.GetString(row),
            Int8Array a => (long)a.GetValue(row)!.Value,
            Int16Array a => (long)a.GetValue(row)!.Value,
            Int32Array a => (long)a.GetValue(row)!.Value,
            Int64Array a => a.GetValue(row)!.Value,
            FloatArray a => (double)a.GetValue(row)!.Value,
            DoubleArray a => a.GetValue(row)!.Value,
            BooleanArray a => a.GetValue(row)!.Value,
            Decimal128Array a => a.GetValue(row)!.Value,
            Date32Array a => a.GetDateTime(row)!.Value,
            TimestampArray a => a.GetTimestamp(row)!.Value.UtcDateTime,
            _ => array.GetType().Name + ":unsupported",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (connection != null)
        {
            try { await RunAsync("DROP TABLE IF EXISTS probe"); } catch (Exception ex) when (ex is AdbcException or InvalidOperationException) { }
            connection.Dispose();
        }
        database?.Dispose();
    }
}
