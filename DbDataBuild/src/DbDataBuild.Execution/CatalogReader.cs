using System.Data;
using System.Globalization;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>A source whose dialect says `catalog: none` was asked for its tables and columns. The tool does not guess them: they are declared by hand.</summary>
public sealed class CatalogUnavailableException(string dialect, string? message = null) : Exception(message ?? $"The dialect `{dialect}` says `catalog: none`: this source's tables and columns are not read from a catalog, so declare them by hand.")
{
    public string Dialect { get; } = dialect;

    /// <summary>The reason in a few words, for a line of `connection inspect`; the message says what to do about it.</summary>
    public string Brief { get; private init; } = "the dialect says `catalog: none`, so the tool does not look for tables";

    /// <summary>A dialect that says `catalog: odbc` on a driver that does not offer the catalog function the read needs.</summary>
    public static CatalogUnavailableException OdbcWithoutFunction(string dialect, string function) => new(dialect,
        $"The ODBC driver does not offer {function}, which the dialect `{dialect}` reads its catalog with (`catalog: odbc`): this source's tables and columns are not read from a catalog, so declare them by hand (or, if the driver has `information_schema`, say `catalog: information_schema`).")
    { Brief = $"the ODBC driver does not offer {function}, which `catalog: odbc` reads the catalog with" };

    /// <summary>A Flight SQL server that answers neither the metadata calls nor `information_schema`: the dialect did not say `catalog: none`, the server has none to read.</summary>
    public static CatalogUnavailableException FlightSqlWithoutCatalog(string dialect) => new(dialect,
        "The Flight SQL server offers neither the metadata calls (GetTables and the rest) nor `information_schema`: this source's tables and columns are not read from a catalog, so declare them by hand (or say `catalog: none` in the dialect to skip the attempt).")
    { Brief = "the Flight SQL server offers no catalog to read (neither metadata calls nor information_schema)" };
}

/// <summary>Reads the live shape of the objects under a schema name, through the read session (DESIGN.md 12.2: catalog hashing on every target).</summary>
public static class CatalogReader
{
    // DuckDB: the standard views, with the parameters by position (schema, object, object). Its indexes are not read: an ART index is rare in a file that is read, and its definition is a CREATE INDEX text.
    private const string DuckDbColumns = @"
SELECT t.table_name, t.table_type, c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_nullable
FROM information_schema.tables t
JOIN information_schema.columns c ON c.table_catalog = t.table_catalog AND c.table_schema = t.table_schema AND c.table_name = t.table_name
WHERE t.table_catalog = current_database() AND t.table_schema = ? AND t.table_type IN ('BASE TABLE', 'VIEW') AND (? = '' OR t.table_name = ?)
ORDER BY t.table_name, c.ordinal_position";

    // A system known only by its ODBC driver (or any dialect whose profile says `catalog: information_schema`): the standard views and nothing else, the parameters by position (schema, then the object when one is asked for).
    private const string AnsiColumnsHead = @"
SELECT t.table_name, t.table_type, c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_nullable
FROM information_schema.tables t
JOIN information_schema.columns c ON c.table_schema = t.table_schema AND c.table_name = t.table_name
WHERE t.table_schema = ? AND t.table_type IN ('BASE TABLE', 'VIEW')";
    private const string AnsiColumnsTail = " ORDER BY t.table_name, c.ordinal_position";

    /// <param name="objectName">Only this object of the schema name (a step that checks what it just did reads one object, not the hundreds a schema name may hold); null reads them all.</param>
    public static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadObjectsAsync(ReadSession read, string target, string schema, CancellationToken ct = default, string? objectName = null)
    {
        if (target == "duckdb") return await ReadDuckDbObjectsAsync(read, schema, objectName, ct);
        if (target == "flightsql") return await ReadFlightSqlObjectsAsync(read, schema, objectName, ct);
        if (target == "odbc") return read.Dialect.Catalog == Models.CatalogStyle.Odbc ? await ReadOdbcObjectsAsync(read, schema, objectName, ct) : await ReadInformationSchemaObjectsAsync(read, schema, objectName, ct);
        // SQL Server and PostgreSQL: the shared catalog readers, run through the read session (plan 13, B04)
        return await SharedCatalog.ReadObjectsAsync(read, target == "postgres", schema, objectName, ct);
    }

    private static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadDuckDbObjectsAsync(ReadSession read, string schema, string? objectName, CancellationToken ct)
    {
        var name = objectName ?? "";
        var rows = await read.QueryAsync(DuckDbColumns, [new GateParameter("schema", DbType.String, schema), new GateParameter("object", DbType.String, name), new GateParameter("object", DbType.String, name)], ct);
        var objects = new Dictionary<string, (ObjectKind Kind, List<ColumnShape> Columns)>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var table = (string)r[0]!;
            if (!objects.TryGetValue(table, out var o)) objects[table] = o = ((string)r[1]! == "VIEW" ? ObjectKind.View : ObjectKind.Table, []);
            var declared = ((string)r[4]!).ToLowerInvariant();
            var type = declared.Contains('(') ? declared[..declared.IndexOf('(')].Trim() : declared;          // DECIMAL(18,3) is the type decimal with a precision and scale of its own
            var decimalType = type == "decimal";
            o.Columns.Add(new((string)r[3]!, type, Int(r[5]), decimalType ? Int(r[6]) : null, decimalType ? Int(r[7]) : null, (string?)r[8] == "YES", null, null));
        }
        return objects.ToDictionary(kv => $"{schema}.{kv.Key}", kv => new ObjectShape(schema, kv.Key, kv.Value.Kind, kv.Value.Columns, []));
    }

    // A Flight SQL server: its metadata calls first (the Arrow schema of each table), then `information_schema` as the dialect says; a dialect that says `catalog: none` reads neither.
    private static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadFlightSqlObjectsAsync(ReadSession read, string schema, string? objectName, CancellationToken ct)
    {
        if (read.Dialect.Catalog == Models.CatalogStyle.None) throw new CatalogUnavailableException(read.Dialect.Name);
        if (read.Metadata is { } metadata && await metadata.TablesAsync(schema, objectName, ct) is { } tables)
            return tables.ToDictionary(t => $"{schema}.{t.Name}", t => new ObjectShape(schema, t.Name, t.IsView ? ObjectKind.View : ObjectKind.Table, t.Columns, []));
        try { return await ReadInformationSchemaObjectsAsync(read, schema, objectName, ct); }
        catch (Exception ex) when (FlightSqlFailure.Rpc(ex) is { } rpc && rpc.StatusCode is not (Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.Unauthenticated or Grpc.Core.StatusCode.PermissionDenied or Grpc.Core.StatusCode.DeadlineExceeded or Grpc.Core.StatusCode.Cancelled))
        { throw CatalogUnavailableException.FlightSqlWithoutCatalog(read.Dialect.Name); }
    }

    /// <summary>
    /// One table or view of an origin by the name a declaration spells it. The name is first looked up as written; if there is no such object and the origin is an ODBC or Flight SQL one, as the dialect's
    /// `identifier_case` says an unquoted name becomes (<see cref="Models.DialectProfile.Fold"/>). <c>StoredAs</c> is the catalog's own spelling (`SRC.ITEMS`) when only the folded name was there, else null; a null
    /// shape means no such object under either spelling.
    /// </summary>
    public static async Task<(ObjectShape? Shape, string? StoredAs)> FindAsync(ReadSession read, string target, string schema, string name, CancellationToken ct = default)
    {
        var exact = await ReadObjectsAsync(read, target, schema, ct);
        if (exact.TryGetValue($"{schema}.{name}", out var found)) return (found, null);
        if (target is not ("odbc" or "flightsql")) return (null, null);
        var (foldedSchema, foldedName) = (read.Dialect.Fold(schema), read.Dialect.Fold(name));
        if (foldedSchema == schema && foldedName == name) return (null, null);
        var folded = await ReadObjectsAsync(read, target, foldedSchema, ct);
        return folded.TryGetValue($"{foldedSchema}.{foldedName}", out var stored) ? (stored, $"{foldedSchema}.{foldedName}") : (null, null);
    }

    // `catalog: odbc`: the tables and columns the driver's own catalog functions (SQLTables, SQLColumns) report, for a system with no information_schema
    private static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadOdbcObjectsAsync(ReadSession read, string schema, string? objectName, CancellationToken ct)
    {
        if (read.Metadata is not { } metadata || await metadata.TablesAsync(schema, objectName, ct) is not { } tables) throw CatalogUnavailableException.OdbcWithoutFunction(read.Dialect.Name, "SQLColumns");
        return tables.ToDictionary(t => $"{schema}.{t.Name}", t => new ObjectShape(schema, t.Name, t.IsView ? ObjectKind.View : ObjectKind.Table, t.Columns, []));
    }

    private static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadInformationSchemaObjectsAsync(ReadSession read, string schema, string? objectName, CancellationToken ct)
    {
        if (read.Dialect.Catalog == Models.CatalogStyle.None) throw new CatalogUnavailableException(read.Dialect.Name);
        var parameters = new List<GateParameter> { new("schema", DbType.String, schema) };
        if (objectName != null) parameters.Add(new("object", DbType.String, objectName));
        var rows = await read.QueryAsync(AnsiColumnsHead + (objectName != null ? " AND t.table_name = ?" : "") + AnsiColumnsTail, parameters, ct);
        var objects = new Dictionary<string, (ObjectKind Kind, List<ColumnShape> Columns)>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var table = r[0]?.ToString() ?? "";
            if (!objects.TryGetValue(table, out var o)) objects[table] = o = (r[1]?.ToString() == "VIEW" ? ObjectKind.View : ObjectKind.Table, []);
            var type = (r[4]?.ToString() ?? "").ToLowerInvariant();
            if (type.Contains('(')) type = type[..type.IndexOf('(')].Trim();
            var decimalType = type is "decimal" or "numeric";
            o.Columns.Add(new(r[3]?.ToString() ?? "", type, Int(r[5]), decimalType ? Int(r[6]) : null, decimalType ? Int(r[7]) : null, string.Equals(r[8]?.ToString(), "YES", StringComparison.OrdinalIgnoreCase), null, null));
        }
        return objects.ToDictionary(kv => $"{schema}.{kv.Key}", kv => new ObjectShape(schema, kv.Key, kv.Value.Kind, kv.Value.Columns, []));
    }

    private static int? Int(object? v) => v == null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);
}
