using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataKit.Dialect;

namespace DbDataKit.Metadata;

/// <summary>Options shared by the <see cref="DbConnection"/>-based readers.</summary>
public sealed record CatalogReaderOptions
{
    /// <summary>Command timeout for every catalog query. Zero means the provider default.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Also report the index behind a PRIMARY KEY (and the unique-constraint ones, flagged <see cref="IndexInfo.BacksConstraint"/>). Off by default: a consumer that creates indexes from a shape does not want the key's own index in the list;
    /// one that compares a live structure with a declared one (Build) does.
    /// </summary>
    public bool IncludeConstraintIndexes { get; init; }
}

/// <summary>
/// The generic catalog reader: standard INFORMATION_SCHEMA over any <see cref="DbConnection"/>, dialect-aware (parameter markers come from
/// the dialect, names are quoted through it). Table listings are filtered in memory so a <see cref="TableFilter"/> means the same on every
/// engine. Engine-specific readers derive from it and replace the SQL (the row layouts below are the contract) or whole steps.
/// The connection is borrowed: it is opened if closed and left open; the caller disposes it.
/// </summary>
/// <remarks>
/// Row layouts the SQL members must return:
/// columns (name, ordinal, data_type, nullable, default, char_length, precision, scale, collation[, is_identity, is_computed]);
/// primary key (constraint, column, ordinal); foreign key (constraint, ordinal, column, ref_catalog, ref_schema, ref_table, ref_column, update_rule, delete_rule);
/// index (name, unique, ordinal, column, descending[, included, backs_constraint, compression, partitions]; a row with <c>included</c> true is an INCLUDE column, not a key column). Rules are text: CASCADE, SET NULL, SET DEFAULT, RESTRICT, NO ACTION.
/// A DbException while reading keys, foreign keys or indexes does not fail the shape: the matching <see cref="ShapeSupport"/> flag is
/// false and a warning carries the provider's message (so permission errors are visible, not silent).
/// </remarks>
public class InformationSchemaCatalogReader : ICatalogReader
{
    private readonly CatalogReaderOptions options;

    /// <summary>The options this reader was made with, for the engine readers' own queries.</summary>
    protected CatalogReaderOptions Options => options;

    public InformationSchemaCatalogReader(DbConnection connection, ISqlDialect dialect, CatalogReaderOptions? options = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        this.options = options ?? new CatalogReaderOptions();
    }

    protected DbConnection Connection { get; }
    protected ISqlDialect Dialect { get; }

    public virtual CatalogCapabilities Capabilities => new(
        Catalogs: true, Schemas: true, ForeignKeys: true, Indexes: false, RowEstimates: false, Procedures: false,
        IdentityAndComputed: false, Describe: DescribeStrategy.WrappedZeroRowProbe);

    // ---- SQL (override per engine) ----------------------------------------------------------------------------------

    protected virtual string CatalogsSql => "SELECT catalog_name FROM information_schema.schemata";

    /// <summary>Rows: catalog, schema.</summary>
    protected virtual string SchemasSql => "SELECT catalog_name, schema_name FROM information_schema.schemata";

    /// <summary>Rows: catalog, schema, name, type text (see <see cref="MapKind"/>).</summary>
    protected virtual string TablesSql => "SELECT table_catalog, table_schema, table_name, table_type FROM information_schema.tables";

    protected virtual (string Sql, object?[] Args) ColumnsQuery(TableRef table)
    {
        var (where, args) = Where(table, "table_catalog", "table_schema", "table_name");
        return ("SELECT column_name, ordinal_position, data_type, is_nullable, column_default, character_maximum_length, numeric_precision, numeric_scale, collation_name "
            + $"FROM information_schema.columns WHERE {where} ORDER BY ordinal_position", args);
    }

    protected virtual (string Sql, object?[] Args)? PrimaryKeyQuery(TableRef table)
    {
        var (where, args) = Where(table, "tc.table_catalog", "tc.table_schema", "tc.table_name");
        return ("SELECT tc.constraint_name, kcu.column_name, kcu.ordinal_position FROM information_schema.table_constraints tc "
            + "JOIN information_schema.key_column_usage kcu ON kcu.constraint_catalog = tc.constraint_catalog AND kcu.constraint_schema = tc.constraint_schema "
            + "AND kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name "
            + $"WHERE tc.constraint_type = 'PRIMARY KEY' AND {where} ORDER BY kcu.ordinal_position", args);
    }

    protected virtual (string Sql, object?[] Args)? ForeignKeysQuery(TableRef table)
    {
        var (where, args) = Where(table, "kcu.table_catalog", "kcu.table_schema", "kcu.table_name");
        return ("SELECT kcu.constraint_name, kcu.ordinal_position, kcu.column_name, kcu2.table_catalog, kcu2.table_schema, kcu2.table_name, kcu2.column_name, rc.update_rule, rc.delete_rule "
            + "FROM information_schema.key_column_usage kcu "
            + "JOIN information_schema.referential_constraints rc ON rc.constraint_catalog = kcu.constraint_catalog AND rc.constraint_schema = kcu.constraint_schema AND rc.constraint_name = kcu.constraint_name "
            + "JOIN information_schema.key_column_usage kcu2 ON kcu2.constraint_catalog = rc.unique_constraint_catalog AND kcu2.constraint_schema = rc.unique_constraint_schema "
            + "AND kcu2.constraint_name = rc.unique_constraint_name AND kcu2.ordinal_position = kcu.position_in_unique_constraint "
            + $"WHERE {where} ORDER BY kcu.constraint_name, kcu.ordinal_position", args);
    }

    /// <summary>INFORMATION_SCHEMA has no index view: null means "not available".</summary>
    protected virtual (string Sql, object?[] Args)? IndexesQuery(TableRef table) => null;

    protected virtual (string Sql, object?[] Args)? RowEstimateQuery(TableRef table) => null;

    /// <summary>Whether the catalog column of INFORMATION_SCHEMA views is meaningful (it is a constant on some engines).</summary>
    protected virtual bool UsesCatalogColumn => true;

    protected virtual TableKind MapKind(string? type) => (type ?? "").Trim().ToUpperInvariant() switch
    {
        "BASE TABLE" or "TABLE" or "FOREIGN" or "FOREIGN TABLE" => TableKind.Table,
        "VIEW" => TableKind.View,
        "MATERIALIZED VIEW" => TableKind.MaterializedView,
        "SYSTEM TABLE" or "SYSTEM VIEW" => TableKind.SystemTable,
        "LOCAL TEMPORARY" or "GLOBAL TEMPORARY" or "TEMPORARY" or "TEMP" => TableKind.TemporaryTable,
        _ => TableKind.Other,
    };

    protected virtual bool IsSystemSchema(string? schema) =>
        schema is not null && (schema.Equals("information_schema", StringComparison.OrdinalIgnoreCase) || schema.Equals("pg_catalog", StringComparison.OrdinalIgnoreCase)
            || schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || schema.StartsWith("pg_", StringComparison.OrdinalIgnoreCase));

    protected virtual bool IsSystemCatalog(string? catalog) => false;

    /// <summary>The identifying columns of a table as a WHERE fragment with {n} placeholders (see <see cref="QueryAsync"/>).</summary>
    protected (string Where, object?[] Args) Where(TableRef table, string catalogColumn, string schemaColumn, string nameColumn)
    {
        var conditions = new List<string>();
        var args = new List<object?>();
        if (table.Catalog is not null && UsesCatalogColumn) { conditions.Add($"{catalogColumn} = {{{args.Count}}}"); args.Add(table.Catalog); }
        if (table.Schema is not null) { conditions.Add($"{schemaColumn} = {{{args.Count}}}"); args.Add(table.Schema); }
        conditions.Add($"{nameColumn} = {{{args.Count}}}");
        args.Add(table.Name);
        return (string.Join(" AND ", conditions), [.. args]);
    }

    // ---- ICatalogReader ---------------------------------------------------------------------------------------------

    public virtual async Task<ServerFacts> GetServerFactsAsync(CancellationToken cancellationToken = default)
    {
        string? version = null;
        try { await EnsureOpenAsync(cancellationToken).ConfigureAwait(false); version = Connection.ServerVersion; }
        catch (Exception ex) when (ex is DbException or NotSupportedException or InvalidOperationException) { /* facts are best effort */ }
        return ServerFactsProbe.FromDialect(Dialect, ProductName, version);
    }

    /// <summary>The product name reported in <see cref="ServerFacts"/>; null for the generic reader.</summary>
    protected virtual string? ProductName => null;

    public virtual async IAsyncEnumerable<string> ListCatalogsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(CatalogsSql, [], cancellationToken).ConfigureAwait(false);
        foreach (string name in rows.Select(r => r.Text(0)).OfType<string>().Where(c => !IsSystemCatalog(c)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            yield return name;
    }

    /// <summary>The schema list itself (empty schemas and system catalogs included), ordered by catalog then name.</summary>
    public virtual async IAsyncEnumerable<SchemaRef> ListSchemasAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(SchemasSql, [], cancellationToken).ConfigureAwait(false);
        var schemas = rows.Where(r => r.Text(1) is not null).Select(r => new SchemaRef(r.Text(0), r.Text(1)!)).Distinct();
        foreach (var s in schemas.OrderBy(s => s.Catalog, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)) yield return s;
    }

    public virtual async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = await ReadColumnsAsync(table, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0) throw new KeyNotFoundException($"No columns found for {table} (table missing, or not visible to this login).");
        return columns;
    }

    public virtual async IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        filter ??= TableFilter.All;
        var matches = filter.Compile();
        var rows = await QueryAsync(TablesSql, [], cancellationToken).ConfigureAwait(false);
        var tables = new List<TableRef>();
        foreach (var row in rows)
        {
            string? name = row.Text(2);
            if (name is null) continue;
            string? catalog = row.Text(0), schema = row.Text(1);
            if (IsSystemCatalog(catalog) && !filter.IncludeSystemObjects) continue;
            var kind = MapKind(row.Text(3));
            if (IsSystemSchema(schema) || IsSystemCatalog(catalog)) kind = TableKind.SystemTable;
            var table = new TableRef(catalog, schema, name, kind);
            if (matches(table)) tables.Add(table);
        }
        foreach (var table in tables.OrderBy(t => t.Catalog, StringComparer.Ordinal).ThenBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
            yield return table;
    }

    public virtual async Task<TableShape> GetShapeAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = await ReadColumnsAsync(table, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0) throw new KeyNotFoundException($"No columns found for {table} (table missing, or not visible to this login).");

        var warnings = new List<string>();
        bool pkOk = true, fkOk = true, ixOk = true;
        KeyInfo? pk = null;
        IReadOnlyList<ForeignKeyInfo> fks = [];
        IReadOnlyList<IndexInfo> indexes = [];
        long? estimate = null;

        try { pk = await ReadPrimaryKeyAsync(table, cancellationToken).ConfigureAwait(false); }
        catch (DbException ex) { pkOk = false; warnings.Add($"primary key not read: {ex.Message}"); }
        if (table.Kind != TableKind.View)
        {
            try { fks = await ReadForeignKeysAsync(table, cancellationToken).ConfigureAwait(false); }
            catch (DbException ex) { fkOk = false; warnings.Add($"foreign keys not read: {ex.Message}"); }
            try
            {
                var found = await ReadIndexesAsync(table, cancellationToken).ConfigureAwait(false);
                if (found is null) ixOk = false; else indexes = found;
            }
            catch (DbException ex) { ixOk = false; warnings.Add($"indexes not read: {ex.Message}"); }
        }
        bool estimateOk = Capabilities.RowEstimates;
        try { estimate = await ReadRowEstimateAsync(table, cancellationToken).ConfigureAwait(false); }
        catch (DbException ex) { estimateOk = false; warnings.Add($"row estimate not read: {ex.Message}"); }

        return new TableShape(table, columns, pk, fks, indexes, estimate,
            new ShapeSupport(pkOk, fkOk && Capabilities.ForeignKeys, ixOk && Capabilities.Indexes, estimateOk, Capabilities.IdentityAndComputed),
            warnings.Count == 0 ? null : warnings);
    }

    public virtual Task<QueryShape> DescribeQueryAsync(string sql, CancellationToken cancellationToken = default) =>
        QueryProbe.WrappedZeroRowAsync(Connection, Dialect, sql, options.CommandTimeout, cancellationToken);

    // ---- Steps ------------------------------------------------------------------------------------------------------

    protected virtual async Task<IReadOnlyList<ColumnInfo>> ReadColumnsAsync(TableRef table, CancellationToken cancellationToken)
    {
        var (sql, args) = ColumnsQuery(table);
        var rows = await QueryAsync(sql, args, cancellationToken).ConfigureAwait(false);
        return ColumnsOf(rows);
    }

    private IReadOnlyList<ColumnInfo> ColumnsOf(IEnumerable<CatalogRow> rows)
    {
        var columns = new List<ColumnInfo>();
        foreach (var r in rows)
        {
            string native = TypeText.Compose(r.Text(2) ?? "unknown", r.Int(5), r.Int(6), r.Int(7));
            columns.Add(new ColumnInfo(r.Text(0)!, r.Int(1) ?? columns.Count + 1, TypeText.ToCanonical(Dialect, native), native, r.Bool(3, whenNull: true),
                r.Text(4), IsIdentity: r.Count > 9 && r.Bool(9), IsComputed: r.Count > 10 && r.Bool(10), Collation: r.Text(8)));
        }
        return columns;
    }

    // ---- A whole schema ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the column and index queries of this reader can be asked without a table name (an empty <see cref="TableRef.Name"/>) and then end each row with the table's name. A reader that says so reads a schema
    /// in a fixed number of queries; any other reads it table by table.
    /// </summary>
    protected virtual bool ReadsWholeSchemas => false;

    /// <inheritdoc cref="ICatalogReader.ReadSchemaAsync"/>
    public virtual async Task<IReadOnlyList<TableShape>> ReadSchemaAsync(string? catalog, string schema, string? table = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        var listed = new List<TableRef>();
        foreach (var row in await QueryAsync(TablesSql, [], cancellationToken).ConfigureAwait(false))
        {
            string? name = row.Text(2), rowCatalog = row.Text(0), rowSchema = row.Text(1);
            if (name is null || !string.Equals(rowSchema, schema, StringComparison.Ordinal)) continue;
            if (catalog is not null && UsesCatalogColumn && !string.Equals(rowCatalog, catalog, StringComparison.Ordinal)) continue;
            if (table is not null && !string.Equals(name, table, StringComparison.Ordinal)) continue;
            var kind = MapKind(row.Text(3));
            if (kind is TableKind.Table or TableKind.View) listed.Add(new TableRef(rowCatalog, rowSchema, name, kind));
        }
        listed.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        if (!ReadsWholeSchemas)
        {
            var shapes = new List<TableShape>();
            foreach (var t in listed)
            {
                try { shapes.Add(await GetShapeAsync(t, cancellationToken).ConfigureAwait(false)); }
                catch (KeyNotFoundException) { /* listed, but its columns are not visible to this login */ }
            }
            return shapes;
        }

        var everything = new TableRef(catalog, schema, table ?? "");
        var (columnsSql, columnsArgs) = ColumnsQuery(everything);
        var columnRows = (await QueryAsync(columnsSql, columnsArgs, cancellationToken).ConfigureAwait(false)).ToLookup(r => r.Text(r.Count - 1) ?? "", StringComparer.Ordinal);
        ILookup<string, CatalogRow>? indexRows = null;
        if (Capabilities.Indexes && IndexesQuery(everything) is var (indexSql, indexArgs))
            indexRows = (await QueryAsync(indexSql, indexArgs, cancellationToken).ConfigureAwait(false)).ToLookup(r => r.Text(r.Count - 1) ?? "", StringComparer.Ordinal);

        var support = new ShapeSupport(PrimaryKeys: false, ForeignKeys: false, Indexes: indexRows is not null, RowEstimate: false, IdentityAndComputed: Capabilities.IdentityAndComputed);
        var result = new List<TableShape>();
        foreach (var t in listed)
        {
            var columns = ColumnsOf(columnRows[t.Name].OrderBy(r => r.Int(1) ?? 0));
            if (columns.Count == 0) continue;
            var indexes = t.Kind == TableKind.View || indexRows is null ? [] : IndexesOf(indexRows[t.Name]);
            result.Add(new TableShape(t, columns, null, [], indexes, null, support));
        }
        return result;
    }

    protected virtual async Task<KeyInfo?> ReadPrimaryKeyAsync(TableRef table, CancellationToken cancellationToken)
    {
        if (PrimaryKeyQuery(table) is not var (sql, args)) return null;
        var rows = await QueryAsync(sql, args, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return null;
        return new KeyInfo(rows[0].Text(0), rows.OrderBy(r => r.Int(2) ?? 0).Select(r => r.Text(1)!).ToList());
    }

    protected virtual async Task<IReadOnlyList<ForeignKeyInfo>> ReadForeignKeysAsync(TableRef table, CancellationToken cancellationToken)
    {
        if (ForeignKeysQuery(table) is not var (sql, args)) return [];
        var rows = await QueryAsync(sql, args, cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => r.Text(0) ?? "").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var ordered = g.OrderBy(r => r.Int(1) ?? 0).ToList();
            var first = ordered[0];
            return new ForeignKeyInfo(string.IsNullOrEmpty(g.Key) ? null : g.Key, ordered.Select(r => r.Text(2)!).ToList(),
                new TableRef(first.Text(3), first.Text(4), first.Text(5) ?? "", TableKind.Table), ordered.Select(r => r.Text(6)!).ToList(),
                Rule(first.Text(7)), Rule(first.Text(8)));
        }).ToList();
    }

    /// <summary>Null means the engine offers no way to read indexes.</summary>
    protected virtual async Task<IReadOnlyList<IndexInfo>?> ReadIndexesAsync(TableRef table, CancellationToken cancellationToken)
    {
        if (IndexesQuery(table) is not var (sql, args)) return null;
        var rows = await QueryAsync(sql, args, cancellationToken).ConfigureAwait(false);
        return IndexesOf(rows);
    }

    private static IReadOnlyList<IndexInfo> IndexesOf(IEnumerable<CatalogRow> rows) =>
        rows.GroupBy(r => r.Text(0) ?? "").OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var keys = g.Where(r => !r.Bool(5)).OrderBy(r => r.Int(2) ?? 0).Select(r => new IndexColumn(r.Text(3) ?? "<expression>", r.Bool(4))).ToList();
                var include = g.Where(r => r.Bool(5)).OrderBy(r => r.Int(2) ?? 0).Select(r => r.Text(3) ?? "<expression>").ToList();
                return new IndexInfo(g.Key, first.Bool(1), keys, include.Count == 0 ? null : include, first.Bool(6), first.Text(7), first.Int(8));
            })
            .ToList();

    protected virtual async Task<long?> ReadRowEstimateAsync(TableRef table, CancellationToken cancellationToken)
    {
        if (RowEstimateQuery(table) is not var (sql, args)) return null;
        var rows = await QueryAsync(sql, args, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0].Long(0);
    }

    protected static ReferentialAction Rule(string? text) => (text ?? "").Trim().ToUpperInvariant().Replace('_', ' ') switch
    {
        "NO ACTION" => ReferentialAction.NoAction,
        "CASCADE" => ReferentialAction.Cascade,
        "SET NULL" => ReferentialAction.SetNull,
        "SET DEFAULT" => ReferentialAction.SetDefault,
        "RESTRICT" => ReferentialAction.Restrict,
        _ => ReferentialAction.Unknown,
    };

    // ---- Execution --------------------------------------------------------------------------------------------------

    protected async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (Connection.State == ConnectionState.Closed) await Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a catalog query. <c>{0}</c>, <c>{1}</c>... in the text become the dialect's parameter markers for <paramref name="args"/>
    /// (<c>@p0</c>, <c>$1</c>, <c>?</c>, <c>:p0</c>); values always travel as parameters, never as text.
    /// </summary>
    protected virtual async Task<IReadOnlyList<CatalogRow>> QueryAsync(string sql, object?[] args, CancellationToken cancellationToken, CommandBehavior behavior = CommandBehavior.Default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Connection.CreateCommand();
        command.CommandText = BindMarkers(Dialect, sql, args.Length);
        if (options.CommandTimeout > TimeSpan.Zero) command.CommandTimeout = (int)Math.Ceiling(options.CommandTimeout.TotalSeconds);
        AddParameters(command, Dialect, args);

        await using var reader = await command.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<CatalogRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            rows.Add(new CatalogRow(names, values));
        }
        return rows;
    }

    internal static string BindMarkers(ISqlDialect dialect, string sql, int count)
    {
        for (int i = 0; i < count; i++) sql = sql.Replace("{" + i + "}", dialect.ParameterName(i), StringComparison.Ordinal);
        return sql;
    }

    internal static void AddParameters(DbCommand command, ISqlDialect dialect, object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var p = command.CreateParameter();
            // Named styles bind by name; numbered and positional styles bind by order (the provider ignores the name).
            p.ParameterName = dialect.ParameterStyle switch
            {
                ParameterStyle.NamedAt => dialect.ParameterName(i),
                ParameterStyle.NamedColon => dialect.ParameterName(i)[1..],
                _ => "",
            };
            p.Value = args[i] ?? DBNull.Value;
            command.Parameters.Add(p);
        }
    }
}
