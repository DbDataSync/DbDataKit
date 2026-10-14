using System.Text.RegularExpressions;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using Arrow.Flight.Protocol.Sql;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Data;
using Grpc.Core;

namespace DbDataProxy.Server.Flight;

// Flight SQL catalog metadata (catalogs, schemas, tables, table types, primary keys, server info).
// Table metadata is read from INFORMATION_SCHEMA through the data source's own executor, so it works for SQL Server,
// DuckDB and ODBC sources (including the 32-bit worker) whose database exposes those standard views. Filtering is done
// here in C#, never by pasting client text into SQL.
public sealed partial class ProxyFlightServer
{
    private static readonly string[] TableTypes = ["TABLE", "VIEW"];

    private const string SchemataSql = "SELECT catalog_name, schema_name FROM information_schema.schemata";
    private const string TablesSql = "SELECT table_catalog, table_schema, table_name, table_type FROM information_schema.tables";

    private const string PrimaryKeysSql =
        "SELECT kcu.table_catalog, kcu.table_schema, kcu.table_name, kcu.column_name, kcu.constraint_name, kcu.ordinal_position " +
        "FROM information_schema.table_constraints tc " +
        "JOIN information_schema.key_column_usage kcu " +
        "ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema AND tc.table_name = kcu.table_name " +
        "WHERE tc.constraint_type = 'PRIMARY KEY'";

    private static readonly Schema CatalogsSchema = new([Str("catalog_name", false)], null);
    private static readonly Schema DbSchemasSchema = new([Str("catalog_name", true), Str("db_schema_name", false)], null);
    private static readonly new Schema TableTypesSchema = new([Str("table_type", false)], null);

    private static readonly new Schema PrimaryKeysSchema = new(
        [Str("catalog_name", true), Str("db_schema_name", true), Str("table_name", false), Str("column_name", false),
         Str("key_name", true), new Field("key_sequence", Int32Type.Default, false)], null);

    // ---- Flight infos: the ticket for a metadata command is the command itself ----

    private static Task<FlightInfo> MetadataInfo(Schema schema, FlightDescriptor descriptor) =>
        Task.FromResult(new FlightInfo(schema, descriptor, [new FlightEndpoint(new FlightTicket(descriptor.Command), [])], -1, -1));

    protected override Task<FlightInfo> GetCatalogFlightInfo(CommandGetCatalogs command, FlightDescriptor descriptor, ServerCallContext context) =>
        MetadataInfo(CatalogsSchema, descriptor);

    protected override Task<FlightInfo> GetDbSchemaFlightInfo(CommandGetDbSchemas command, FlightDescriptor descriptor, ServerCallContext context) =>
        MetadataInfo(DbSchemasSchema, descriptor);

    protected override Task<FlightInfo> GetTablesFlightInfo(CommandGetTables command, FlightDescriptor descriptor, ServerCallContext context) =>
        MetadataInfo(GetTableSchema(command.IncludeSchema), descriptor);

    protected override Task<FlightInfo> GetTableTypesFlightInfo(CommandGetTableTypes command, FlightDescriptor descriptor, ServerCallContext context) =>
        MetadataInfo(TableTypesSchema, descriptor);

    protected override Task<FlightInfo> GetPrimaryKeysFlightInfo(CommandGetPrimaryKeys command, FlightDescriptor descriptor, ServerCallContext context) =>
        MetadataInfo(PrimaryKeysSchema, descriptor);

    protected override Task<FlightInfo> GetSqlFlightInfo(CommandGetSqlInfo command, FlightDescriptor descriptor, ServerCallContext context) =>
        MetadataInfo(SqlInfoSchema, descriptor);

    // ---- Data ----

    protected override async Task DoGetCatalog(CommandGetCatalogs command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var rows = await LoadSchemataAsync(ResolveSource(context), context.CancellationToken).ConfigureAwait(false);
        var catalogs = rows.Select(r => r[0]).Where(c => c is not null).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        await WriteAsync(writer, CatalogsSchema, [Strings(catalogs)], catalogs.Count).ConfigureAwait(false);
    }

    protected override async Task DoGetDbSchema(CommandGetDbSchemas command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var rows = await LoadSchemataAsync(ResolveSource(context), context.CancellationToken).ConfigureAwait(false);
        var schemaFilter = Pattern(command.HasDbSchemaFilterPattern ? command.DbSchemaFilterPattern : null);
        var selected = rows
            .Where(r => r[1] is not null && (!command.HasCatalog || r[0] == command.Catalog) && schemaFilter(r[1]!))
            .OrderBy(r => r[0], StringComparer.Ordinal).ThenBy(r => r[1], StringComparer.Ordinal).ToList();
        await WriteAsync(writer, DbSchemasSchema, [Strings(selected.Select(r => r[0])), Strings(selected.Select(r => r[1]))], selected.Count)
            .ConfigureAwait(false);
    }

    protected override async Task DoGetTables(CommandGetTables command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        var source = catalog.Resolve(principal, context.RequestHeaders.GetValue(DataSourceHeader));

        var schemaFilter = Pattern(command.HasDbSchemaFilterPattern ? command.DbSchemaFilterPattern : null);
        var tableFilter = Pattern(command.HasTableNameFilterPattern ? command.TableNameFilterPattern : null);
        var wantedTypes = command.TableTypes.Count == 0 ? null : command.TableTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = (await LoadTablesAsync(source, context.CancellationToken).ConfigureAwait(false))
            .Select(r => new[] { r[0], r[1], r[2], FlightTableType(r[3]) })
            .Where(r => r[2] is not null && r[3] is not null
                        && (!command.HasCatalog || r[0] == command.Catalog)
                        && schemaFilter(r[1] ?? "") && tableFilter(r[2]!)
                        && (wantedTypes is null || wantedTypes.Contains(r[3]!)))
            .OrderBy(r => r[0], StringComparer.Ordinal).ThenBy(r => r[1], StringComparer.Ordinal).ThenBy(r => r[2], StringComparer.Ordinal)
            .ToList();

        var arrays = new List<IArrowArray>
        {
            Strings(rows.Select(r => r[0])), Strings(rows.Select(r => r[1])), Strings(rows.Select(r => r[2])), Strings(rows.Select(r => r[3])),
        };
        if (command.IncludeSchema)
        {
            var quote = await LoadIdentifierQuoteAsync(source, context.CancellationToken).ConfigureAwait(false);
            var declared = await LoadDeclaredColumnsAsync(source, context.CancellationToken).ConfigureAwait(false);
            var builder = new BinaryArray.Builder();
            foreach (var r in rows)
                builder.Append((ReadOnlySpan<byte>)await SerializedTableSchemaAsync(source, quote, declared, r[0], r[1], r[2]!, context.CancellationToken).ConfigureAwait(false));
            arrays.Add(builder.Build());
        }
        await WriteAsync(writer, GetTableSchema(command.IncludeSchema), arrays, rows.Count).ConfigureAwait(false);
    }

    protected override async Task DoGetTableType(CommandGetTableTypes command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        _ = context.GetPrincipal();
        await WriteAsync(writer, TableTypesSchema, [Strings(TableTypes)], TableTypes.Length).ConfigureAwait(false);
    }

    protected override async Task DoGetPrimaryKeys(CommandGetPrimaryKeys command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var rows = (await LoadPrimaryKeysAsync(ResolveSource(context), context.CancellationToken).ConfigureAwait(false))
            .Where(r => r[2] == command.Table
                        && (!command.HasCatalog || r[0] == command.Catalog)
                        && (!command.HasDbSchema || r[1] == command.DbSchema))
            .OrderBy(r => int.TryParse(r[5], out int n) ? n : 0)
            .ToList();

        var sequence = new Int32Array.Builder();
        foreach (var r in rows) sequence.Append(int.TryParse(r[5], out int n) ? n : 0);

        await WriteAsync(writer, PrimaryKeysSchema,
        [
            Strings(rows.Select(r => r[0])), Strings(rows.Select(r => r[1])), Strings(rows.Select(r => r[2])),
            Strings(rows.Select(r => r[3])), Strings(rows.Select(r => r[4])), sequence.Build(),
        ], rows.Count).ConfigureAwait(false);
    }

    // ---- Server info ----

    private static readonly new Schema SqlInfoSchema = new(
    [
        new Field("info_name", UInt32Type.Default, false),
        new Field("value", new UnionType(
            [
                new Field("string_value", StringType.Default, false),
                new Field("bool_value", BooleanType.Default, false),
                new Field("bigint_value", Int64Type.Default, false),
                new Field("int32_bitmask", Int32Type.Default, false),
                new Field("string_list", new ListType(new Field("item", StringType.Default, true)), false),
                new Field("int32_to_int32_list_map", new MapType(Int32Type.Default, new ListType(new Field("item", Int32Type.Default, true)), false), false),
            ],
            [0, 1, 2, 3, 4, 5], UnionMode.Dense), false),
    ], null);

    // Enum values the Flight SQL spec defines for SQL_IDENTIFIER_CASE / SQL_QUOTED_IDENTIFIER_CASE and SQL_NULL_ORDERING.
    private const long CaseInsensitive = 1;
    private const long NullsSortedLow = 1;
    private const long NullsSortedAtEnd = 3;

    /// <summary>What the server tells clients about itself and the data source they selected.</summary>
    private sealed class SqlInfoFacts
    {
        public Dictionary<uint, string> Strings { get; } = [];
        public Dictionary<uint, bool> Bools { get; } = [];
        public Dictionary<uint, long> Longs { get; } = [];

        public IEnumerable<uint> All => Strings.Keys.Concat(Bools.Keys).Concat(Longs.Keys).OrderBy(k => k);
    }

    // GetSqlInfo is per data source: the x-datasource header picks the backend whose product, version, quoting and
    // collation-related facts are reported. With no data source selected (several are configured and the client has not
    // said which) it answers for the proxy alone.
    protected override async Task DoGetSqlInfo(CommandGetSqlInfo command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var principal = context.GetPrincipal();
        string? requestedSource = context.RequestHeaders.GetValue(DataSourceHeader);
        ResolvedDataSource? source = null;
        try
        {
            source = catalog.Resolve(principal, requestedSource);
        }
        catch (RpcException ex) when (string.IsNullOrWhiteSpace(requestedSource) && ex.StatusCode == StatusCode.InvalidArgument)
        {
            // Nothing selected and nothing to default to.
        }

        var facts = await DescribeAsync(source, context.CancellationToken).ConfigureAwait(false);
        var requested = command.Info.Count == 0
            ? facts.All.ToList()
            : command.Info.Select(i => (uint)i).Where(i => facts.Strings.ContainsKey(i) || facts.Bools.ContainsKey(i) || facts.Longs.ContainsKey(i)).ToList();

        var names = new UInt32Array.Builder();
        var typeIds = new ArrowBuffer.Builder<byte>();
        var offsets = new ArrowBuffer.Builder<int>();
        var stringChild = new StringArray.Builder();
        var boolChild = new BooleanArray.Builder();
        var longChild = new Int64Array.Builder();
        foreach (uint id in requested)
        {
            names.Append(id);
            if (facts.Strings.TryGetValue(id, out var s))
            {
                typeIds.Append(0);
                offsets.Append(stringChild.Length);
                stringChild.Append(s);
            }
            else if (facts.Bools.TryGetValue(id, out bool b))
            {
                typeIds.Append(1);
                offsets.Append(boolChild.Length);
                boolChild.Append(b);
            }
            else
            {
                typeIds.Append(2);
                offsets.Append(longChild.Length);
                longChild.Append(facts.Longs[id]);
            }
        }

        var unionType = (UnionType)SqlInfoSchema.GetFieldByName("value").DataType;
        IArrowArray[] children =
        [
            stringChild.Build(), boolChild.Build(), longChild.Build(), new Int32Array.Builder().Build(),
            new ListArray.Builder(StringType.Default).Build(), new MapArray.Builder((MapType)unionType.Fields[5].DataType).Build(),
        ];
        var union = new DenseUnionArray(unionType, requested.Count, children, typeIds.Build(), offsets.Build(), 0, 0);
        await WriteAsync(writer, SqlInfoSchema, [names.Build(), union], requested.Count).ConfigureAwait(false);
    }

    private async Task<SqlInfoFacts> DescribeAsync(ResolvedDataSource? source, CancellationToken cancellationToken)
    {
        var facts = new SqlInfoFacts();
        string proxyVersion = typeof(ProxyFlightServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        facts.Strings[(uint)SqlInfo.FlightSqlServerName] = "DbDataProxy";
        facts.Strings[(uint)SqlInfo.FlightSqlServerVersion] = proxyVersion;
        facts.Strings[(uint)SqlInfo.FlightSqlServerArrowVersion] = typeof(Apache.Arrow.Schema).Assembly.GetName().Version?.ToString(3) ?? "";
        facts.Bools[(uint)SqlInfo.FlightSqlServerReadOnly] = false;
        facts.Bools[(uint)SqlInfo.FlightSqlServerSql] = true;
        facts.Bools[(uint)SqlInfo.FlightSqlServerSubstrait] = false;
        facts.Bools[(uint)SqlInfo.FlightSqlServerCancel] = false;
        facts.Bools[(uint)SqlInfo.FlightSqlServerBulkIngestion] = false;
        if (source is null) return facts;

        // Read-only means no statement can change data through this data source's update path; queries are still
        // limited only by the database account (see the README), so use a read-only login for real protection.
        facts.Bools[(uint)SqlInfo.FlightSqlServerReadOnly] = !source.Options.AllowWrites;

        string? quote = null;
        switch (source.Options.Provider.ToLowerInvariant())
        {
            case "sqlserver":
                facts.Strings[(uint)SqlInfo.FlightSqlServerName] = "Microsoft SQL Server";
                quote = "\"";
                facts.Longs[(uint)SqlInfo.SqlIdentifierCase] = CaseInsensitive;
                facts.Longs[(uint)SqlInfo.SqlQuotedIdentifierCase] = CaseInsensitive;
                facts.Longs[(uint)SqlInfo.SqlNullOrdering] = NullsSortedLow;
                break;
            case "duckdb":
                facts.Strings[(uint)SqlInfo.FlightSqlServerName] = "DuckDB";
                quote = "\"";
                facts.Longs[(uint)SqlInfo.SqlIdentifierCase] = CaseInsensitive;
                facts.Longs[(uint)SqlInfo.SqlQuotedIdentifierCase] = CaseInsensitive;
                facts.Longs[(uint)SqlInfo.SqlNullOrdering] = NullsSortedAtEnd;
                break;
        }

        // Ask the database itself for what a static table cannot know: the version, and for ODBC the product and
        // the driver's identifier quote. A database that cannot be reached still gets the static answers.
        if (source.Executor is IMetadataProvider provider)
        {
            try
            {
                var dialect = await provider.GetDialectAsync(cancellationToken).ConfigureAwait(false);
                if (dialect.DbmsName is { Length: > 0 } name) facts.Strings[(uint)SqlInfo.FlightSqlServerName] = name;
                if (dialect.DbmsVersion is { Length: > 0 } version) facts.Strings[(uint)SqlInfo.FlightSqlServerVersion] = version;
                if (dialect.Quote is { } q) quote = q.Prefix;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Could not read the dialect of data source {Source}", source.Name);
            }
        }
        if (quote is not null) facts.Strings[(uint)SqlInfo.SqlIdentifierQuoteChar] = quote;
        return facts;
    }

    // ---- Helpers ----

    private static Field Str(string name, bool nullable) => new(name, StringType.Default, nullable);

    private static StringArray Strings(IEnumerable<string?> values)
    {
        var builder = new StringArray.Builder();
        foreach (var v in values)
        {
            if (v is null) builder.AppendNull();
            else builder.Append(v);
        }
        return builder.Build();
    }

    private static async Task WriteAsync(FlightServerRecordBatchStreamWriter writer, Schema schema, IReadOnlyList<IArrowArray> columns, int length)
    {
        await writer.SetupStream(schema).ConfigureAwait(false);
        await writer.WriteAsync(new RecordBatch(schema, columns, length)).ConfigureAwait(false);
    }

    /// <summary>SQL LIKE patterns (% and _) as a case-insensitive matcher; null matches everything.</summary>
    private static Func<string, bool> Pattern(string? like)
    {
        if (string.IsNullOrEmpty(like)) return _ => true;
        var regex = new Regex("^" + Regex.Escape(like).Replace("%", ".*").Replace("_", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return regex.IsMatch;
    }

    private static string? FlightTableType(string? databaseType) => databaseType?.ToUpperInvariant() switch
    {
        "BASE TABLE" => "TABLE",
        null => null,
        var other => other,
    };

    private ResolvedDataSource ResolveSource(ServerCallContext context) =>
        catalog.Resolve(context.GetPrincipal(), context.RequestHeaders.GetValue(DataSourceHeader));

    /// <summary>True when metadata should come from the driver's catalog API (ODBC SQLTables) instead of INFORMATION_SCHEMA.</summary>
    private static bool UseCatalogApi(ResolvedDataSource source) => source.Options.Metadata.ToLowerInvariant() switch
    {
        "odbc" => true,
        "informationschema" => false,
        _ => source.Options.Provider.Equals("odbc", StringComparison.OrdinalIgnoreCase),
    };

    private static async Task<List<string?[]>> LoadSchemataAsync(ResolvedDataSource source, CancellationToken cancellationToken)
    {
        if (!UseCatalogApi(source))
            return await QueryRowsAsync(source, SchemataSql, cancellationToken).ConfigureAwait(false);

        // The ODBC catalog API has no separate schema listing here; the schemas are those that contain tables.
        var tables = await LoadTablesAsync(source, cancellationToken).ConfigureAwait(false);
        return tables.Select(t => new[] { t[0], t[1] }).DistinctBy(r => (r[0], r[1])).ToList();
    }

    private static async Task<List<string?[]>> LoadTablesAsync(ResolvedDataSource source, CancellationToken cancellationToken)
    {
        if (!UseCatalogApi(source))
            return await QueryRowsAsync(source, TablesSql, cancellationToken).ConfigureAwait(false);

        if (source.Executor is not IMetadataProvider provider)
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Data source '{source.Name}' cannot list tables through the catalog API."));
        try
        {
            var tables = await provider.ListTablesAsync(source.Options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            return tables.Select(t => new string?[] { t.Catalog, t.Schema, t.Name, t.Type }).ToList();
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            throw ToRpcException(ex);
        }
    }

    // The ODBC catalog functions have no portable primary-key listing in ADO.NET, so those sources report none.
    private static Task<List<string?[]>> LoadPrimaryKeysAsync(ResolvedDataSource source, CancellationToken cancellationToken) =>
        UseCatalogApi(source) ? Task.FromResult(new List<string?[]>()) : QueryRowsAsync(source, PrimaryKeysSql, cancellationToken);

    private static async Task<List<string?[]>> QueryRowsAsync(ResolvedDataSource source, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var result = await source.Executor
                .ExecuteQueryAsync(new QueryRequest(sql, 4096, source.Options.CommandTimeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
            var rows = new List<string?[]>();
            await foreach (var batch in result.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
                for (int r = 0; r < batch.Length; r++)
                {
                    var row = new string?[batch.ColumnCount];
                    for (int c = 0; c < row.Length; c++) row[c] = ArrowText.Get(batch.Column(c), r);
                    rows.Add(row);
                }
            return rows;
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            throw ToRpcException(ex);
        }
    }

    /// <summary>Looks up the declared columns of one table, by name; null when the database cannot say.</summary>
    private delegate Task<IReadOnlyDictionary<string, ColumnEntry>?> DeclaredColumns(
        string? catalogName, string? schemaName, string table, CancellationToken cancellationToken);

    private const string ColumnsSql =
        "SELECT table_catalog, table_schema, table_name, column_name, is_nullable, numeric_precision, numeric_scale FROM information_schema.columns";

    /// <summary>
    /// A zero-row query cannot always say how a column was declared (DuckDB reports every decimal as 0/0, nothing says
    /// NOT NULL), so precision, scale and nullability come from the catalog: INFORMATION_SCHEMA, or SQLColumns for ODBC.
    /// </summary>
    private static async Task<DeclaredColumns> LoadDeclaredColumnsAsync(ResolvedDataSource source, CancellationToken cancellationToken)
    {
        if (UseCatalogApi(source))
        {
            if (source.Executor is not IMetadataProvider provider) return (_, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, ColumnEntry>?>(null);
            return async (catalogName, schemaName, table, ct) =>
            {
                try { return ByName(await provider.ListColumnsAsync(catalogName, schemaName, table, ct).ConfigureAwait(false)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
            };
        }

        Dictionary<(string?, string?, string), List<ColumnEntry>> byTable = [];
        try
        {
            foreach (var r in await QueryRowsAsync(source, ColumnsSql, cancellationToken).ConfigureAwait(false))
            {
                if (r[2] is null || r[3] is null) continue;
                var key = (r[0], r[1], r[2]);
                if (!byTable.TryGetValue(key, out var list)) byTable[key] = list = [];
                list.Add(new ColumnEntry(r[3], !string.Equals(r[4], "NO", StringComparison.OrdinalIgnoreCase),
                    int.TryParse(r[5], out int p) ? p : null, int.TryParse(r[6], out int s) ? s : null));
            }
        }
        catch (RpcException)
        {
            // No INFORMATION_SCHEMA.COLUMNS: keep what the zero-row query reported.
        }
        return (catalogName, schemaName, table, _) => Task.FromResult<IReadOnlyDictionary<string, ColumnEntry>?>(
            byTable.TryGetValue((catalogName, schemaName, table), out var columns) ? ByName(columns) : null);
    }

    private static Dictionary<string, ColumnEntry> ByName(IEnumerable<ColumnEntry> columns)
    {
        var map = new Dictionary<string, ColumnEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns) map[c.Name] = c;
        return map;
    }

    /// <summary>Overlays declared nullability and decimal precision/scale on a schema read from a zero-row query.</summary>
    internal static Schema ApplyDeclaredColumns(Schema schema, IReadOnlyDictionary<string, ColumnEntry>? declared)
    {
        if (declared is null) return schema;
        var fields = schema.FieldsList.Select(field =>
        {
            if (!declared.TryGetValue(field.Name, out var column)) return field;
            var type = field.DataType;
            if (type is Decimal128Type && column.Precision is > 0 and <= 38 && column.Scale is >= 0 && column.Scale <= column.Precision)
                type = new Decimal128Type(column.Precision.Value, column.Scale.Value);
            return new Field(field.Name, type, column.Nullable, field.Metadata);
        }).ToList();
        return new Schema(fields, schema.Metadata);
    }

    /// <summary>The Arrow schema of a table (from a zero-row query), serialized the way Flight SQL expects.</summary>
    private static async Task<byte[]> SerializedTableSchemaAsync(
        ResolvedDataSource source, Func<string, string> quote, DeclaredColumns declared, string? catalogName, string? schemaName, string table,
        CancellationToken cancellationToken)
    {
        // Some ODBC drivers report a catalog that is not valid in SQL (the text driver reports the directory path), so
        // fall back from the fully qualified name to schema.table and finally the bare table name.
        string?[][] candidates = [[catalogName, schemaName, table], [schemaName, table], [table]];
        foreach (var parts in candidates.DistinctBy(p => string.Join('\0', p)))
        {
            string name = string.Join('.', parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => quote(p!)));
            try
            {
                await using var result = await source.Executor
                    .ExecuteQueryAsync(new QueryRequest($"SELECT * FROM {name} WHERE 1 = 0", 1, source.Options.CommandTimeoutSeconds), cancellationToken)
                    .ConfigureAwait(false);
                var columns = await declared(catalogName, schemaName, table, cancellationToken).ConfigureAwait(false);
                return SchemaExtensions.SerializeSchema(ApplyDeclaredColumns(result.Schema, columns));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try the next, less qualified name.
            }
        }
        // A table whose schema cannot be read (permissions, exotic types) is reported with an empty schema.
        return SchemaExtensions.SerializeSchema(new Schema([], null));
    }

    private static readonly Func<string, string> StandardQuote = id => "\"" + id.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// ODBC sources are quoted with the driver's own identifier character (some use backticks or brackets, some none);
    /// everything else, and any driver that cannot say, gets standard SQL double quotes.
    /// </summary>
    private static async Task<Func<string, string>> LoadIdentifierQuoteAsync(ResolvedDataSource source, CancellationToken cancellationToken)
    {
        if (!UseCatalogApi(source) || source.Executor is not IMetadataProvider provider) return StandardQuote;
        try
        {
            var quote = (await provider.GetDialectAsync(cancellationToken).ConfigureAwait(false)).Quote;
            return quote is null ? id => id : quote.Apply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StandardQuote;
        }
    }
}
