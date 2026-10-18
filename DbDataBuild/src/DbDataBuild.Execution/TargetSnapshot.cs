using System.Data;
using System.Globalization;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>Everything planning reads from a target: live shapes of the objects under the managed schema names and the tool's own records about them.</summary>
/// <param name="LastViewStatementHashes">Newest successful DDL statement hash per object (`ddl_log`); for a view this is the statement that created or altered it.</param>
/// <param name="LastLoadDefinitionHashes">Definition hash of the newest successful load per model (`run_log`).</param>
/// <param name="Acknowledged">`code|object|detail` of every block a person acknowledged (`block_log`).</param>
public sealed record TargetSnapshot(
    IReadOnlyDictionary<string, ObjectShape> Live,
    IReadOnlySet<string> Schemas,
    IReadOnlyDictionary<string, string> RecordedShapeHashes,
    IReadOnlyDictionary<string, string> LastViewStatementHashes,
    IReadOnlyDictionary<string, string> LastLoadDefinitionHashes,
    IReadOnlySet<string> Acknowledged);

/// <summary>The result of running a resolver: one value, or the reason it was not usable.</summary>
public sealed record ResolverValue(string? Value, string? Error);

public static class TargetSnapshotReader
{
    /// <summary>
    /// What planning reads: the live shapes from the data connection (<paramref name="read"/>), and the tool's records about that connection from its tracking store (<paramref name="trackingRead"/>, which may be another
    /// connection, or the same session). With no tracking (<paramref name="scope"/> null) there are no records: every object that exists is judged against the declaration alone.
    /// </summary>
    public static async Task<TargetSnapshot> ReadAsync(ReadSession read, ReadSession? trackingRead, TrackingScope? scope, string target, IEnumerable<string> managedSchemas, CancellationToken ct = default)
    {
        var schemaRows = await read.QueryAsync(target == "postgres" ? "SELECT schema_name FROM information_schema.schemata" : "SELECT name FROM sys.schemas", null, ct);
        var schemas = schemaRows.Select(r => (string)r[0]!).ToHashSet(StringComparer.Ordinal);

        var live = new Dictionary<string, ObjectShape>(StringComparer.Ordinal);
        foreach (var schema in managedSchemas.Distinct(StringComparer.Ordinal).Where(schemas.Contains))
            foreach (var (name, shape) in await CatalogReader.ReadObjectsAsync(read, target, schema, ct)) live[name] = shape;

        if (trackingRead == null || scope == null)
            return new TargetSnapshot(live, schemas, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), new HashSet<string>());

        string C(string n) => TrackingDdl.For(scope.Engine).Quote(n);
        var t = (string table) => $"{C(scope.SchemaName)}.{C(table)}";
        var byConnection = new[] { new GateParameter("connection", DbType.String, scope.Connection) };

        var recorded = await TrackingStore.LatestShapeHashesAsync(trackingRead, scope, ct);

        // the newest ok row of each object (and of each model): one pass with a window function. The log tables only grow, and a correlated MAX for each row (what this was) read the whole table once per row,
        // which stopped `plan` with a timeout after a couple of hundred thousand rows (entry 116)
        var ddl = await trackingRead.QueryAsync(
            $"SELECT x.{C("object_name")}, x.{C("statement_hash")} FROM (SELECT d.{C("object_name")}, d.{C("statement_hash")}, ROW_NUMBER() OVER (PARTITION BY d.{C("object_name")} ORDER BY d.{C("executed_utc")} DESC) AS newest " +
            $"FROM {t("ddl_log")} d WHERE d.{C("connection")} = @connection AND d.{C("status")} = 'ok') x WHERE x.newest = 1", byConnection, ct);
        var views = ddl.ToDictionary(r => (string)r[0]!, r => ((string)r[1]!).Trim(), StringComparer.Ordinal);

        var runs = await trackingRead.QueryAsync(
            $"SELECT x.{C("model")}, x.{C("definition_hash")} FROM (SELECT r.{C("model")}, r.{C("definition_hash")}, ROW_NUMBER() OVER (PARTITION BY r.{C("model")} ORDER BY r.{C("started_utc")} DESC) AS newest " +
            $"FROM {t("run_log")} r WHERE r.{C("connection")} = @connection AND r.{C("status")} = 'ok' AND r.{C("definition_hash")} IS NOT NULL) x WHERE x.newest = 1", byConnection, ct);
        var loads = runs.ToDictionary(r => (string)r[0]!, r => ((string)r[1]!).Trim(), StringComparer.Ordinal);

        var acks = await trackingRead.QueryAsync($"SELECT b.{C("code")}, b.{C("model")}, b.{C("detail")} FROM {t("block_log")} b WHERE b.{C("connection")} = @connection AND b.{C("ack_utc")} IS NOT NULL", byConnection, ct);
        var acknowledged = acks.Select(r => $"{r[0]}|{r[1]}|{r[2]}").ToHashSet(StringComparer.Ordinal);

        return new TargetSnapshot(live, schemas, recorded, views, loads, acknowledged);
    }

    /// <summary>Runs a committed resolver on the read login. Exactly one row and one column is required; anything else is reported, never guessed at.</summary>
    /// <summary>`MIN` and `MAX` of one column of one table, as plan-time text (a single read-only SELECT through the read session). Names are quoted for the target.</summary>
    /// <summary>A resolver, a range's bounds and a copy's newest value each scan a table (a MAX with no index on the column): ten minutes, not the driver's 30 seconds.</summary>
    public const int ScanTimeoutSeconds = 600;

    public static async Task<(string? Min, string? Max, string? Error)> ColumnBoundsAsync(ReadSession read, string target, string objectName, string column, string parameterType, CancellationToken ct = default)
    {
        var ddl = TrackingDdl.For(target);
        var dot = objectName.LastIndexOf('.');
        var table = dot < 0 ? ddl.Quote(objectName) : $"{ddl.Quote(objectName[..dot])}.{ddl.Quote(objectName[(dot + 1)..])}";
        var c = ddl.Quote(column);
        var min = await RunResolverAsync(read, $"SELECT MIN({c}) FROM {table}", parameterType, ct);
        var max = await RunResolverAsync(read, $"SELECT MAX({c}) FROM {table}", parameterType, ct);
        return (min.Value, max.Value, min.Error ?? max.Error);
    }

    /// <summary>`MAX` of one column of the destination of an incremental copy, as plan-time text; with a slice, only of the rows of that origin (the value is quoted as a literal: it comes from the project's own files).</summary>
    public static async Task<ResolverValue> MaxAsync(ReadSession read, string target, string objectName, string column, string parameterType, string? sliceColumn, string? sliceValue, CancellationToken ct = default)
    {
        var ddl = TrackingDdl.For(target);
        var dot = objectName.LastIndexOf('.');
        var table = dot < 0 ? ddl.Quote(objectName) : $"{ddl.Quote(objectName[..dot])}.{ddl.Quote(objectName[(dot + 1)..])}";
        var where = sliceColumn == null ? "" : $" WHERE {ddl.Quote(sliceColumn)} = '{(sliceValue ?? "").Replace("'", "''")}'";
        return await RunResolverAsync(read, $"SELECT MAX({ddl.Quote(column)}) FROM {table}{where}", parameterType, ct);
    }

    public static async Task<ResolverValue> RunResolverAsync(ReadSession read, string resolverText, string parameterType, CancellationToken ct = default)
    {
        IReadOnlyList<IReadOnlyList<object?>> rows;
        try { rows = await read.QueryAsync(resolverText, null, ct, timeoutSeconds: ScanTimeoutSeconds); }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, $"failed to run ({ex.GetType().Name})");   // type only: driver messages can quote data
        }
        if (rows.Count != 1) return new(null, $"returned {rows.Count} rows, not exactly one");
        if (rows[0].Count != 1) return new(null, $"returned {rows[0].Count} columns, not exactly one");
        var v = rows[0][0];
        return v switch
        {
            null => new(null, null),
            DateTime dt when parameterType.Trim().Equals("DATE", StringComparison.OrdinalIgnoreCase) => new(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null),
            DateTime dt => new(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture), null),
            DateOnly d => new(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null),
            DateTimeOffset dto => new(dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture), null),
            sbyte or byte or short or ushort or int or uint or long or ulong => new(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), null),
            _ => new(Convert.ToString(v, CultureInfo.InvariantCulture), null),
        };
    }
}
