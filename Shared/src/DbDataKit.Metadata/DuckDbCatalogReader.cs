using System.Data.Common;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>
/// DuckDB through its <c>duckdb_*()</c> table functions (complete type text, constraints with column order, indexes, estimated sizes) and
/// <c>DESCRIBE</c> for query shapes. Works over any DuckDB ADO.NET connection (DuckDB.NET); the library is not referenced here. The
/// built-in <c>system</c> catalog is a system object; the <c>temp</c> catalog holds temporary tables. DuckDB has no identity or computed-column reporting in these
/// functions, so <see cref="ShapeSupport.IdentityAndComputed"/> is false.
/// </summary>
public class DuckDbCatalogReader(DbConnection connection, ISqlDialect? dialect = null, CatalogReaderOptions? options = null)
    : InformationSchemaCatalogReader(connection, dialect ?? DialectCatalog.For(EngineId.DuckDb), options)
{
    public override CatalogCapabilities Capabilities => new(
        Catalogs: true, Schemas: true, ForeignKeys: true, Indexes: true, RowEstimates: true, Procedures: false,
        IdentityAndComputed: false, Describe: DescribeStrategy.EngineDescribeStatement);

    protected override string? ProductName => "DuckDB";

    protected override string CatalogsSql => "SELECT database_name FROM duckdb_databases() WHERE NOT internal ORDER BY database_name";

    // duckdb_tables()/duckdb_views() also list the built-in views of the "system" catalog (information_schema, pg_catalog, duckdb_*), which INFORMATION_SCHEMA does not.
    protected override string TablesSql =>
        "SELECT database_name, schema_name, table_name, CASE WHEN database_name = 'temp' THEN 'LOCAL TEMPORARY' ELSE 'BASE TABLE' END FROM duckdb_tables() "
        + "UNION ALL SELECT database_name, schema_name, view_name, CASE WHEN database_name = 'temp' THEN 'LOCAL TEMPORARY' ELSE 'VIEW' END FROM duckdb_views()";

    protected override bool IsSystemCatalog(string? catalog) => catalog is not null && catalog.Equals("system", StringComparison.OrdinalIgnoreCase);

    private static (string Where, object?[] Args) Of(TableRef t, string catalogColumn = "database_name", string schemaColumn = "schema_name", string nameColumn = "table_name")
    {
        var conditions = new List<string>();
        var args = new List<object?>();
        if (t.Catalog is not null) { conditions.Add($"{catalogColumn} = {{{args.Count}}}"); args.Add(t.Catalog); }
        if (t.Schema is not null) { conditions.Add($"{schemaColumn} = {{{args.Count}}}"); args.Add(t.Schema); }
        if (t.Name.Length > 0) { conditions.Add($"{nameColumn} = {{{args.Count}}}"); args.Add(t.Name); }
        else if (t.Catalog is null) conditions.Add($"{catalogColumn} = current_database()");     // a whole schema (ReadSchemaKeysAsync) of the current database: another database may have a schema of that name
        return (string.Join(" AND ", conditions), [.. args]);
    }

    protected override (string Sql, object?[] Args) ColumnsQuery(TableRef t)
    {
        var (where, args) = Of(t);
        // data_type is the full type text (DECIMAL(18,3), VARCHAR, INTEGER[]); length and precision are already inside it.
        return ("SELECT column_name, column_index, data_type, is_nullable, column_default, NULL, NULL, NULL, NULL "
            + $"FROM duckdb_columns() WHERE {where} ORDER BY column_index", args);
    }

    private static string Unnested(string constraintType, string where) =>
        "SELECT constraint_name, col, i, table_name FROM (SELECT constraint_name, table_name, unnest(constraint_column_names) AS col, unnest(range(1, len(constraint_column_names) + 1)) AS i "
        + $"FROM duckdb_constraints() WHERE constraint_type = '{constraintType}' AND {where})";

    protected override (string Sql, object?[] Args)? PrimaryKeyQuery(TableRef t)
    {
        var (where, args) = Of(t);
        return (Unnested("PRIMARY KEY", where) + " ORDER BY i", args);
    }

    protected override (string Sql, object?[] Args)? ForeignKeysQuery(TableRef t)
    {
        var (where, args) = Of(t);
        // duckdb_constraints reports the referenced table and its columns but not its schema: the referenced table is taken from the same schema.
        // {c} and {s} stand for the referenced catalog and schema; they are literals taken from the table reference, quoted as DuckDB string literals.
        string catalog = t.Catalog is null ? "NULL" : Literal(t.Catalog), schema = t.Schema is null ? "NULL" : Literal(t.Schema);
        return ($"SELECT constraint_name, i, col, {catalog}, {schema}, referenced_table, ref_col, 'NO ACTION', 'NO ACTION', table_name FROM (SELECT constraint_name, table_name, referenced_table, "
            + "unnest(constraint_column_names) AS col, unnest(referenced_column_names) AS ref_col, unnest(range(1, len(constraint_column_names) + 1)) AS i "
            + $"FROM duckdb_constraints() WHERE constraint_type = 'FOREIGN KEY' AND {where}) ORDER BY constraint_name, i", args);
    }

    private string Literal(string text) => Dialect.Literal(text, CanonicalType.String());

    protected override async Task<IReadOnlyList<IndexInfo>?> ReadIndexesAsync(TableRef table, CancellationToken cancellationToken)
    {
        var (where, args) = Of(table);
        var rows = await QueryAsync($"SELECT index_name, is_unique, expressions FROM duckdb_indexes() WHERE {where} ORDER BY index_name", args, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new IndexInfo(r.Text(0)!, r.Bool(1), ParseExpressions(r.Text(2)))).ToList();
    }

    /// <summary>duckdb_indexes reports the indexed expressions as text such as <c>[a, b]</c> (older versions: <c>(a, b)</c>); a plain column is its name, anything else is kept as written.</summary>
    internal static IReadOnlyList<IndexColumn> ParseExpressions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        string inner = text.Trim();
        if (inner.Length >= 2 && ((inner[0] == '[' && inner[^1] == ']') || (inner[0] == '(' && inner[^1] == ')'))) inner = inner[1..^1];
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '(' or '[') depth++;
            else if (!quoted && c is ')' or ']') depth--;
            else if (!quoted && depth == 0 && c == ',') { parts.Add(inner[start..i]); start = i + 1; }
        }
        parts.Add(inner[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0)
            .Select(p => new IndexColumn(p.Length >= 2 && p[0] == '"' && p[^1] == '"' ? p[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : p)).ToList();
    }

    protected override (string Sql, object?[] Args)? RowEstimateQuery(TableRef t)
    {
        var (where, args) = Of(t);
        return ($"SELECT estimated_size FROM duckdb_tables() WHERE {where}", args);
    }

    public override async Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync("SELECT version()", [], cancellationToken).ConfigureAwait(false);
        return ServerFactsProbe.FromDialect(Dialect, ProductName, rows.Count > 0 ? rows[0].Text(0)?.TrimStart('v') : null);
    }

    /// <summary><c>DESCRIBE &lt;query&gt;</c>: DuckDB plans the query and reports column names, full type text and nullability without running it.</summary>
    public override async Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        string text = QueryProbe.RequireReadOnly(sql, Dialect);
        var rows = await QueryAsync("DESCRIBE " + text, [], cancellationToken).ConfigureAwait(false);
        var columns = new List<ColumnInfo>(rows.Count);
        foreach (var r in rows)
        {
            string native = r.Text("column_type") ?? "unknown";
            columns.Add(new ColumnInfo(r.Text("column_name") ?? $"column{columns.Count + 1}", columns.Count + 1, TypeText.ToCanonical(Dialect, native), native,
                r.Bool("null", whenNull: true)));
        }
        return new QueryShape(columns, DescribeStrategy.EngineDescribeStatement);
    }
}
