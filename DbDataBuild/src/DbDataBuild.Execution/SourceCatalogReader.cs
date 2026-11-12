using System.Data;
using System.Globalization;

namespace DbDataBuild.Execution;

/// <summary>
/// What `import` reads beyond the shapes <see cref="CatalogReader"/> gives: which schema names exist, each table's primary key (it seeds a new descriptor's grain), its foreign keys and its indexes. Read session only. SQL Server, PostgreSQL and DuckDB
/// are read by the shared catalog readers (<see cref="SharedCatalog"/>); what is written here is the standard-views SQL of an ODBC or Flight SQL source, the Flight SQL and ODBC metadata calls, and DuckDB's indexes.
/// </summary>
public static class SourceCatalogReader
{
    // duckdb_indexes(): the columns of an index are in the CREATE INDEX text (its `expressions` column is a VARCHAR that spells a list); ART indexes are ascending and cannot be INCLUDE-d
    private const string DuckDbIndexes = "SELECT table_name, index_name, is_unique, sql FROM duckdb_indexes() WHERE database_name = current_database() AND schema_name = ? ORDER BY table_name, index_name";

    // A source known by its ODBC driver: the standard views. sys and pg_catalog are the engines' own and are left out whichever engine is behind the driver; every other schema name is the person's to select with the pattern.
    private const string AnsiSchemas = "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('information_schema', 'pg_catalog', 'sys') ORDER BY schema_name";
    private const string AnsiPrimaryKeys = @"
SELECT kcu.table_name, kcu.column_name, kcu.ordinal_position
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu ON kcu.constraint_schema = tc.constraint_schema AND kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = ?
ORDER BY kcu.table_name, kcu.ordinal_position";
    private const string AnsiForeignKeys = @"
SELECT kcu.table_name, kcu.constraint_name, kcu.column_name, rkcu.table_schema, rkcu.table_name, rkcu.column_name, kcu.ordinal_position
FROM information_schema.referential_constraints rc
JOIN information_schema.key_column_usage kcu ON kcu.constraint_schema = rc.constraint_schema AND kcu.constraint_name = rc.constraint_name
JOIN information_schema.key_column_usage rkcu ON rkcu.constraint_schema = rc.unique_constraint_schema AND rkcu.constraint_name = rc.unique_constraint_name AND rkcu.ordinal_position = kcu.position_in_unique_constraint
WHERE kcu.table_schema = ?
ORDER BY kcu.table_name, kcu.constraint_name, kcu.ordinal_position";

    private static bool IsAnsiSource(string target) => target is "odbc" or "flightsql";

    // For a Flight SQL server the metadata calls come first and `information_schema` is the fallback; for an ODBC source whose dialect says `catalog: odbc` the driver's catalog functions are the only way.
    private static IMetadataCatalog? FlightSqlMetadata(ReadSession read, string target) => target == "flightsql" || OdbcFunctions(read, target) ? read.Metadata : null;

    private static bool OdbcFunctions(ReadSession read, string target) => target == "odbc" && read.Dialect.Catalog == Models.CatalogStyle.Odbc;

    private static async Task<T> FlightSqlFallbackAsync<T>(ReadSession read, Func<Task<T>> informationSchema)
    {
        try { return await informationSchema(); }
        catch (Exception ex) when (FlightSqlFailure.Rpc(ex) is { } rpc && rpc.StatusCode is not (Grpc.Core.StatusCode.Unavailable or Grpc.Core.StatusCode.Unauthenticated or Grpc.Core.StatusCode.PermissionDenied or Grpc.Core.StatusCode.DeadlineExceeded or Grpc.Core.StatusCode.Cancelled))
        { throw CatalogUnavailableException.FlightSqlWithoutCatalog(read.Dialect.Name); }
    }

    private static void RequireCatalog(ReadSession read)
    {
        if (read.Dialect.Catalog == Models.CatalogStyle.None) throw new CatalogUnavailableException(read.Dialect.Name);
    }

    public static async Task<IReadOnlyList<string>> SchemasAsync(ReadSession read, string target, CancellationToken ct = default)
    {
        if (IsAnsiSource(target))
        {
            RequireCatalog(read);
            if (FlightSqlMetadata(read, target) is { } metadata && await metadata.SchemasAsync(ct) is { } named) return named.Where(n => n is not ("information_schema" or "pg_catalog" or "sys")).ToList();
            if (OdbcFunctions(read, target)) throw CatalogUnavailableException.OdbcWithoutFunction(read.Dialect.Name, "SQLTables");
            Task<IReadOnlyList<string>> ask() => ReadSchemaNamesAsync(read, ct);
            return target == "flightsql" ? await FlightSqlFallbackAsync(read, ask) : await ask();
        }
        // sys and INFORMATION_SCHEMA are the engine's; db_* are the schema names of fixed database roles; guest is empty. pg_* are PostgreSQL's catalogs and temp schema names. DuckDB has information_schema and pg_catalog.
        var names = await SharedCatalog.ReadSchemaNamesAsync(read, target, ct);
        return target switch
        {
            "duckdb" => names.Where(n => n is not ("information_schema" or "pg_catalog")).ToList(),
            "postgres" => names.Where(n => n != "information_schema" && !n.StartsWith("pg_", StringComparison.Ordinal)).ToList(),
            _ => names.Where(n => !(n.Equals("sys", StringComparison.OrdinalIgnoreCase) || n.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) || n.Equals("guest", StringComparison.OrdinalIgnoreCase) || n.StartsWith("db_", StringComparison.OrdinalIgnoreCase))).ToList(),
        };
    }

    private static async Task<IReadOnlyList<string>> ReadSchemaNamesAsync(ReadSession read, CancellationToken ct) =>
        (await read.QueryAsync(AnsiSchemas, null, ct)).Select(r => r[0]!.ToString()!).ToList();

    /// <summary>Primary key columns, in key order, per table name of the schema name.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PrimaryKeysAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        if (IsAnsiSource(target))
        {
            RequireCatalog(read);
            if (FlightSqlMetadata(read, target) is { } metadata && await metadata.PrimaryKeysAsync(schema, ct) is { } offered) return offered;
            if (OdbcFunctions(read, target)) { read.Note("primary keys were not read: the ODBC driver does not offer SQLPrimaryKeys, so a descriptor's grain is declared by hand"); return new Dictionary<string, IReadOnlyList<string>>(); }
            var ansi = target == "flightsql" ? await FlightSqlFallbackAsync(read, () => read.QueryAsync(AnsiPrimaryKeys, [new GateParameter("schema", DbType.String, schema)], ct))
                : await read.QueryAsync(AnsiPrimaryKeys, [new GateParameter("schema", DbType.String, schema)], ct);
            return ansi.GroupBy(r => r[0]!.ToString()!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(r => Convert.ToInt32(r[2], CultureInfo.InvariantCulture)).Select(r => r[1]!.ToString()!).ToList(), StringComparer.Ordinal);
        }
        return (await KeysAsync(read, target, schema, ct)).PrimaryKeys;
    }

    /// <summary>
    /// The primary keys and foreign keys of a schema together. SQL Server, PostgreSQL and DuckDB are read by the shared readers' schema-wide read (two queries for the schema, not two per table); a source known by its ODBC driver or a Flight SQL server
    /// is read as before, key kind by key kind.
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>> PrimaryKeys, IReadOnlyDictionary<string, IReadOnlyList<State.ForeignKeyShape>> ForeignKeys)> KeysAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        if (IsAnsiSource(target)) return (await PrimaryKeysAsync(read, target, schema, ct), await ForeignKeysAsync(read, target, schema, ct));
        var keys = await SharedCatalog.ReadKeysAsync(read, target, schema, ct);
        return (keys.PrimaryKeys.ToDictionary(kv => kv.Key, kv => kv.Value.Columns, StringComparer.Ordinal),
            keys.ForeignKeys.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<State.ForeignKeyShape>)kv.Value.Select(k => new State.ForeignKeyShape(k.Name ?? "", k.Columns, k.Referenced.Schema ?? schema, k.Referenced.Name, k.ReferencedColumns)).ToList(), StringComparer.Ordinal));
    }

    /// <summary>Foreign keys per table name of the schema name, columns in key order.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<State.ForeignKeyShape>>> ForeignKeysAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        if (IsAnsiSource(target))
        {
            RequireCatalog(read);
            if (FlightSqlMetadata(read, target) is { } metadata && await metadata.ForeignKeysAsync(schema, ct) is { } offered)
                return offered.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<State.ForeignKeyShape>)kv.Value.Select(k => new State.ForeignKeyShape(k.Name, k.Columns, k.ReferencedSchema, k.ReferencedTable, k.ReferencedColumns)).ToList(), StringComparer.Ordinal);
            if (OdbcFunctions(read, target)) { read.Note("foreign keys were not read: the ODBC driver does not offer SQLForeignKeys"); return new Dictionary<string, IReadOnlyList<State.ForeignKeyShape>>(); }
            var ansi = target == "flightsql" ? await FlightSqlFallbackAsync(read, () => read.QueryAsync(AnsiForeignKeys, [new GateParameter("schema", DbType.String, schema)], ct))
                : await read.QueryAsync(AnsiForeignKeys, [new GateParameter("schema", DbType.String, schema)], ct);
            return ansi.GroupBy(r => r[0]!.ToString()!, StringComparer.Ordinal).ToDictionary(
                table => table.Key,
                table => (IReadOnlyList<State.ForeignKeyShape>)table.GroupBy(r => r[1]!.ToString()!, StringComparer.Ordinal).Select(fk =>
                {
                    var ordered = fk.OrderBy(r => Convert.ToInt32(r[6], CultureInfo.InvariantCulture)).ToList();
                    return new State.ForeignKeyShape(fk.Key, ordered.Select(r => r[2]!.ToString()!).ToList(), ordered[0][3]!.ToString()!, ordered[0][4]!.ToString()!, ordered.Select(r => r[5]!.ToString()!).ToList());
                }).ToList(),
                StringComparer.Ordinal);
        }
        return (await KeysAsync(read, target, schema, ct)).ForeignKeys;
    }

    /// <summary>
    /// The schema a DuckDB foreign key points to, from the key's constraint text: what stands between `REFERENCES ` and the referenced table's name (`s.` or `catalog.s.`), or the key's own schema when the text names none.
    /// A name with a dot in it (quoted) cannot be told from a qualifier by its text; the part after the last dot is taken.
    /// </summary>
    internal static string DuckDbReferencedSchema(string? constraintText, string referencedTable, string ownSchema) =>
        DbDataKit.Metadata.DuckDbCatalogReader.ReferencedSchema(constraintText, referencedTable, ownSchema)!;

    /// <summary>
    /// The key columns of a DuckDB index from its `CREATE INDEX` text, or `&lt;expression&gt;` where a key is an expression (the importer says such an index was not exported). Null when the text is not a
    /// CREATE INDEX this reader understands.
    /// </summary>
    internal static IReadOnlyList<string>? DuckDbIndexColumns(string? createIndex)
    {
        if (createIndex == null) return null;
        var on = createIndex.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
        var open = on < 0 ? -1 : createIndex.IndexOf('(', on);
        var close = createIndex.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        var columns = new List<string>();
        var (depth, quoted, from) = (0, false, open + 1);
        for (var i = open + 1; i <= close; i++)
        {
            var c = createIndex[i];
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if ((c == ',' && depth == 0) || (c == ')' && depth == 0 && i == close))
            {
                columns.Add(IndexKey(createIndex[from..i].Trim()));
                from = i + 1;
            }
        }
        return columns;
    }

    private static string IndexKey(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && !text[1..^1].Replace("\"\"", "", StringComparison.Ordinal).Contains('"', StringComparison.Ordinal)) return text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(ch => char.IsLetterOrDigit(ch) || ch == '_') ? text : "<expression>";
    }

    /// <summary>
    /// The indexes of the tables of a schema name where the origin's catalog says them, per table: DuckDB's `duckdb_indexes()`, an ODBC driver's `SQLStatistics`. Null where there is nothing to add: the three engines
    /// give theirs with the columns (<see cref="CatalogReader"/>); a Flight SQL server has no metadata call for indexes and `information_schema` has no indexes view, so those say so in <see cref="ReadSession.CatalogNotes"/>
    /// instead of guessing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<DbDataKit.Schema.PhysicalItem>>?> IndexesAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        static IReadOnlyDictionary<string, IReadOnlyList<DbDataKit.Schema.PhysicalItem>> Items(IEnumerable<(string Table, string Name, bool Unique, IReadOnlyList<string> Columns, IReadOnlyList<string> Include)> indexes) =>
            indexes.GroupBy(i => i.Table, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => (IReadOnlyList<DbDataKit.Schema.PhysicalItem>)g.Select(i => new DbDataKit.Schema.PhysicalItem("index", i.Name, DbDataKit.Schema.IndexText.Canonical(i.Unique, i.Columns, i.Include))).ToList(), StringComparer.Ordinal);

        if (target == "duckdb")
        {
            var rows = await read.QueryAsync(DuckDbIndexes, [new GateParameter("schema", DbType.String, schema)], ct);
            var parsed = rows.Select(r => (Table: (string)r[0]!, Name: (string)r[1]!, Unique: Convert.ToBoolean(r[2], CultureInfo.InvariantCulture), Columns: DuckDbIndexColumns(r[3] as string))).ToList();
            foreach (var bad in parsed.Where(p => p.Columns == null)) read.Note($"index {bad.Name} of {schema}.{bad.Table} was not understood and was not exported");
            return Items(parsed.Where(p => p.Columns != null).Select(p => (p.Table, p.Name, p.Unique, p.Columns!, (IReadOnlyList<string>)[])));
        }
        if (target == "flightsql")
        {
            read.Note("indexes were not read: Flight SQL has no metadata call for indexes, so declare them by hand");
            return null;
        }
        if (target != "odbc" || read.Dialect.Catalog == Models.CatalogStyle.None) return null;
        try
        {
            if (read.Metadata is { } metadata && await metadata.IndexesAsync(schema, ct) is { } offered)
                return Items(offered.Values.SelectMany(x => x).Select(i => (i.Table, i.Name, i.Unique, i.Columns, i.Include)));
            read.Note("indexes were not read: the ODBC driver does not offer SQLStatistics, so declare them by hand");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the catalog functions need the driver manager's library to load and the driver to answer; an import without indexes is still an import
            read.Note($"indexes were not read: SQLStatistics failed ({Failures.Describe(ex)}), so declare them by hand");
        }
        return null;
    }
}
