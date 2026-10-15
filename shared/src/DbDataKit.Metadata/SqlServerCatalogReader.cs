using System.Data.Common;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>
/// SQL Server through the <c>sys</c> views: identity and computed flags, default and computed expressions, collations, primary and foreign keys,
/// indexes and row estimates (<c>sys.dm_db_partition_stats</c>), and <c>sp_describe_first_result_set</c> for query shapes. Works over any
/// <see cref="DbConnection"/> whose provider binds <c>@p0</c>-style named parameters (Microsoft.Data.SqlClient). A catalog on the table or
/// filter is addressed with a three-part name so other databases on the server can be read.
/// </summary>
public class SqlServerCatalogReader(DbConnection connection, ISqlDialect? dialect = null, CatalogReaderOptions? options = null)
    : InformationSchemaCatalogReader(connection, dialect ?? DialectCatalog.For(EngineId.SqlServer), options)
{
    public override CatalogCapabilities Capabilities => new(
        Catalogs: true, Schemas: true, ForeignKeys: true, Indexes: true, RowEstimates: true, Procedures: false,
        IdentityAndComputed: true, Describe: DescribeStrategy.SqlServerDescribeFirstResultSet);

    protected override string? ProductName => "Microsoft SQL Server";

    /// <summary>The database qualifier for sys views: empty for the current database, else the quoted name plus a dot.</summary>
    protected string Prefix(string? catalog) => catalog is null ? "" : CatalogNames.Quote(Dialect, catalog) + ".";

    protected override string CatalogsSql => "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name";

    protected override string TablesSql => TablesSqlFor(null);

    internal string TablesSqlFor(string? catalog) =>
        $"SELECT DB_NAME(), s.name, o.name, o.type FROM {Prefix(catalog)}sys.objects o JOIN {Prefix(catalog)}sys.schemas s ON s.schema_id = o.schema_id WHERE o.type IN ('U', 'V', 'S')";

    protected override TableKind MapKind(string? type) => (type ?? "").Trim().ToUpperInvariant() switch
    {
        "U" => TableKind.Table,
        "V" => TableKind.View,
        "S" => TableKind.SystemTable,
        _ => TableKind.Other,
    };

    protected override bool IsSystemSchema(string? schema) =>
        schema is not null && (schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase));

    private (string Where, object?[] Args) Of(TableRef t, string schemaColumn = "s.name", string nameColumn = "o.name")
    {
        var conditions = new List<string>();
        var args = new List<object?>();
        if (t.Schema is not null) { conditions.Add($"{schemaColumn} = {{{args.Count}}}"); args.Add(t.Schema); }
        conditions.Add($"{nameColumn} = {{{args.Count}}}");
        args.Add(t.Name);
        return (string.Join(" AND ", conditions), [.. args]);
    }

    protected override (string Sql, object?[] Args) ColumnsQuery(TableRef t)
    {
        string p = Prefix(t.Catalog);
        var (where, args) = Of(t);
        // Layout: name, ordinal, type, nullable, default, char_length (chars; -1 is max), precision, scale, collation, identity, computed.
        return ("SELECT c.name, c.column_id, ty.name, c.is_nullable, COALESCE(dc.definition, cc.definition), "
            + "CASE WHEN ty.name IN (N'char', N'varchar', N'binary', N'varbinary') THEN c.max_length WHEN ty.name IN (N'nchar', N'nvarchar') THEN CASE WHEN c.max_length = -1 THEN -1 ELSE c.max_length / 2 END END, "
            + "CASE WHEN ty.name IN (N'decimal', N'numeric') THEN c.precision END, "
            + "CASE WHEN ty.name IN (N'decimal', N'numeric', N'datetime2', N'datetimeoffset', N'time') THEN c.scale END, "
            + "c.collation_name, c.is_identity, c.is_computed "
            + $"FROM {p}sys.columns c JOIN {p}sys.objects o ON o.object_id = c.object_id JOIN {p}sys.schemas s ON s.schema_id = o.schema_id "
            + $"JOIN {p}sys.types ty ON ty.user_type_id = c.system_type_id "
            + $"LEFT JOIN {p}sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id "
            + $"LEFT JOIN {p}sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id "
            + $"WHERE {where} ORDER BY c.column_id", args);
    }

    protected override (string Sql, object?[] Args)? PrimaryKeyQuery(TableRef t)
    {
        string p = Prefix(t.Catalog);
        var (where, args) = Of(t);
        return ("SELECT i.name, c.name, ic.key_ordinal "
            + $"FROM {p}sys.indexes i JOIN {p}sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + $"JOIN {p}sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + $"JOIN {p}sys.objects o ON o.object_id = i.object_id JOIN {p}sys.schemas s ON s.schema_id = o.schema_id "
            + $"WHERE i.is_primary_key = 1 AND ic.is_included_column = 0 AND {where} ORDER BY ic.key_ordinal", args);
    }

    protected override (string Sql, object?[] Args)? ForeignKeysQuery(TableRef t)
    {
        string p = Prefix(t.Catalog);
        var (where, rawArgs) = Of(t, "ps.name", "po.name");
        // The referenced table lives in the same database as the foreign key, which is the catalog named on the table.
        object?[] args = t.Catalog is null ? rawArgs : [.. rawArgs, t.Catalog];
        string db = t.Catalog is null ? "DB_NAME()" : "{" + rawArgs.Length + "}";
        return ($"SELECT fk.name, fkc.constraint_column_id, pc.name, {db}, rs.name, ro.name, rc.name, fk.update_referential_action_desc, fk.delete_referential_action_desc "
            + $"FROM {p}sys.foreign_keys fk JOIN {p}sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id "
            + $"JOIN {p}sys.objects po ON po.object_id = fk.parent_object_id JOIN {p}sys.schemas ps ON ps.schema_id = po.schema_id "
            + $"JOIN {p}sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id "
            + $"JOIN {p}sys.objects ro ON ro.object_id = fk.referenced_object_id JOIN {p}sys.schemas rs ON rs.schema_id = ro.schema_id "
            + $"JOIN {p}sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id "
            + $"WHERE {where} ORDER BY fk.name, fkc.constraint_column_id", args);
    }

    protected override (string Sql, object?[] Args)? IndexesQuery(TableRef t)
    {
        string p = Prefix(t.Catalog);
        var (where, args) = Of(t);
        return ("SELECT i.name, i.is_unique, ic.key_ordinal, c.name, ic.is_descending_key "
            + $"FROM {p}sys.indexes i JOIN {p}sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + $"JOIN {p}sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + $"JOIN {p}sys.objects o ON o.object_id = i.object_id JOIN {p}sys.schemas s ON s.schema_id = o.schema_id "
            + $"WHERE i.is_primary_key = 0 AND i.type > 0 AND i.name IS NOT NULL AND ic.is_included_column = 0 AND ic.key_ordinal > 0 AND {where} ORDER BY i.name, ic.key_ordinal", args);
    }

    protected override (string Sql, object?[] Args)? RowEstimateQuery(TableRef t)
    {
        string p = Prefix(t.Catalog);
        var (where, args) = Of(t);
        return ($"SELECT SUM(ps.row_count) FROM {p}sys.dm_db_partition_stats ps JOIN {p}sys.objects o ON o.object_id = ps.object_id JOIN {p}sys.schemas s ON s.schema_id = o.schema_id "
            + $"WHERE ps.index_id IN (0, 1) AND {where}", args);
    }

    // SQL Server catalog filters name a database: list it with a three-part name rather than ignoring the catalog.
    public override async IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        filter ??= TableFilter.All;
        if (filter.Catalog is null)
        {
            await foreach (var t in base.ListTablesAsync(filter, cancellationToken).ConfigureAwait(false)) yield return t;
            yield break;
        }
        var matches = filter.Compile();
        var rows = await QueryAsync(TablesSqlFor(filter.Catalog), [], cancellationToken).ConfigureAwait(false);
        var tables = rows.Where(r => r.Text(2) is not null)
            .Select(r => new TableRef(filter.Catalog, r.Text(1), r.Text(2)!, IsSystemSchema(r.Text(1)) ? TableKind.SystemTable : MapKind(r.Text(3))))
            .Where(matches).OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal);
        foreach (var t in tables) yield return t;
    }

    public override async Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128))", [], cancellationToken).ConfigureAwait(false);
        var baseFacts = ServerFactsProbe.FromDialect(Dialect, ProductName, rows.Count > 0 ? rows[0].Text(0) : null);
        return baseFacts with { DriverName = rows.Count > 0 ? rows[0].Text(1) : null };
    }

    /// <summary>The statement text and parameters used to describe a query; exposed so the generated SQL can be reviewed and tested offline.</summary>
    internal static (string Sql, object?[] Args) DescribeStatement(string checkedSql) =>
        ("EXEC sp_describe_first_result_set @tsql = {0}, @params = NULL, @browse_information_mode = 0", [checkedSql]);

    /// <summary><c>sp_describe_first_result_set</c>: the server compiles the batch and returns its result columns; nothing is executed.</summary>
    public override async Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        string text = QueryProbe.RequireReadOnly(sql, Dialect);
        var (statement, args) = DescribeStatement(text);
        var rows = await QueryAsync(statement, args, cancellationToken).ConfigureAwait(false);
        var columns = new List<ColumnInfo>();
        foreach (var r in rows.Where(r => !r.Bool("is_hidden")).OrderBy(r => r.Int("column_ordinal") ?? 0))
        {
            string native = r.Text("system_type_name") ?? "unknown";
            columns.Add(new ColumnInfo(r.Text("name") ?? $"column{columns.Count + 1}", r.Int("column_ordinal") ?? columns.Count + 1,
                TypeText.ToCanonical(Dialect, native), native, r.Bool("is_nullable", whenNull: true), null,
                r.Bool("is_identity_column"), r.Bool("is_computed_column"), r.Text("collation_name")));
        }
        return new QueryShape(columns, DescribeStrategy.SqlServerDescribeFirstResultSet);
    }
}
