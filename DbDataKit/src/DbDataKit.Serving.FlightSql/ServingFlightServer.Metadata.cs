using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using Arrow.Flight.Protocol.Sql;
using DbDataKit.Metadata;
using Grpc.Core;

namespace DbDataKit.Serving.FlightSql;

// Flight SQL catalog metadata (catalogs, schemas, tables, table types, primary keys, server info). What the catalog contains comes from the core's catalog service; this file only shapes the Flight SQL responses.
// Filtering is done here in C# with the shared LIKE matcher, never by pasting client text into SQL.
public sealed partial class ServingFlightServer
{
    private static readonly string[] TableTypes = ["TABLE", "VIEW"];

    private static readonly Schema CatalogsSchema = new([Str("catalog_name", false)], null);
    private static readonly Schema DbSchemasSchema = new([Str("catalog_name", true), Str("db_schema_name", false)], null);
    private static readonly new Schema TableTypesSchema = new([Str("table_type", false)], null);

    private static readonly new Schema PrimaryKeysSchema = new(
        [Str("catalog_name", true), Str("db_schema_name", true), Str("table_name", false), Str("column_name", false),
         Str("key_name", true), new Field("key_sequence", Int32Type.Default, false)], null);

    // ---- Flight infos: the ticket for a metadata command is the command itself ----

    private static Task<FlightInfo> MetadataInfo(Schema schema, FlightDescriptor descriptor) =>
        Task.FromResult(new FlightInfo(schema, descriptor, [new FlightEndpoint(new FlightTicket(descriptor.Command), [])], -1, -1));

    protected override Task<FlightInfo> GetCatalogFlightInfo(CommandGetCatalogs command, FlightDescriptor descriptor, ServerCallContext context) => MetadataInfo(CatalogsSchema, descriptor);

    protected override Task<FlightInfo> GetDbSchemaFlightInfo(CommandGetDbSchemas command, FlightDescriptor descriptor, ServerCallContext context) => MetadataInfo(DbSchemasSchema, descriptor);

    protected override Task<FlightInfo> GetTablesFlightInfo(CommandGetTables command, FlightDescriptor descriptor, ServerCallContext context) => MetadataInfo(GetTableSchema(command.IncludeSchema), descriptor);

    protected override Task<FlightInfo> GetTableTypesFlightInfo(CommandGetTableTypes command, FlightDescriptor descriptor, ServerCallContext context) => MetadataInfo(TableTypesSchema, descriptor);

    protected override Task<FlightInfo> GetPrimaryKeysFlightInfo(CommandGetPrimaryKeys command, FlightDescriptor descriptor, ServerCallContext context) => MetadataInfo(PrimaryKeysSchema, descriptor);

    protected override Task<FlightInfo> GetSqlFlightInfo(CommandGetSqlInfo command, FlightDescriptor descriptor, ServerCallContext context) => MetadataInfo(SqlInfoSchema, descriptor);

    // ---- The catalog, as the core describes it ----

    private async Task<List<ServingObject>> ObjectsAsync(ServerCallContext context, bool withColumns = false)
    {
        var serving = context.GetServingContext();
        var objects = new List<ServingObject>();
        try
        {
            await foreach (var o in Services.Catalog.DescribeAsync(serving, [], withColumns, context.CancellationToken).ConfigureAwait(false)) objects.Add(o);
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException) { throw Fail(ex); }
        return objects;
    }

    private static bool IsTable(ServingObject o) => o.Kind is ServingObjectKind.Table or ServingObjectKind.View;

    /// <summary>Every schema the source reports, empty ones and those of system catalogs included, and every schema a table is in.</summary>
    private static IEnumerable<(string? Catalog, string Schema)> SchemataOf(List<ServingObject> objects) =>
        objects.Where(o => o.Kind == ServingObjectKind.Schema).Select(o => (o.Catalog, o.Name))
            .Concat(objects.Where(o => IsTable(o) && o.SchemaName is not null).Select(o => (o.Catalog, o.SchemaName!)))
            .Distinct();

    // ---- Data ----

    protected override async Task DoGetCatalog(CommandGetCatalogs command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var objects = await ObjectsAsync(context).ConfigureAwait(false);
        // A catalog is any the source reports a schema or a table in (ODBC drivers such as the text driver have tables in a catalog and no schema at all).
        var catalogs = objects.Where(o => o.Kind == ServingObjectKind.Catalog).Select(o => o.Name)
            .Concat(SchemataOf(objects).Select(s => s.Catalog)).Concat(objects.Where(IsTable).Select(o => o.Catalog))
            .Where(c => c is not null).Select(c => c!).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        await WriteAsync(writer, CatalogsSchema, [Strings(catalogs)], catalogs.Count).ConfigureAwait(false);
    }

    protected override async Task DoGetDbSchema(CommandGetDbSchemas command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var rows = SchemataOf(await ObjectsAsync(context).ConfigureAwait(false)).ToList();
        var schemaFilter = Pattern(command.HasDbSchemaFilterPattern ? command.DbSchemaFilterPattern : null);
        var selected = rows
            .Where(r => (!command.HasCatalog || r.Catalog == command.Catalog) && schemaFilter(r.Schema))
            .OrderBy(r => r.Catalog, StringComparer.Ordinal).ThenBy(r => r.Schema, StringComparer.Ordinal).ToList();
        await WriteAsync(writer, DbSchemasSchema, [Strings(selected.Select(r => r.Catalog)), Strings(selected.Select(r => (string?)r.Schema))], selected.Count).ConfigureAwait(false);
    }

    protected override async Task DoGetTables(CommandGetTables command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var schemaFilter = Pattern(command.HasDbSchemaFilterPattern ? command.DbSchemaFilterPattern : null);
        var tableFilter = Pattern(command.HasTableNameFilterPattern ? command.TableNameFilterPattern : null);
        var wantedTypes = command.TableTypes.Count == 0 ? null : command.TableTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = (await ObjectsAsync(context, withColumns: command.IncludeSchema).ConfigureAwait(false)).Where(IsTable)
            .Select(o => (Object: o, Type: o.Kind == ServingObjectKind.View ? "VIEW" : "TABLE"))
            .Where(r => (!command.HasCatalog || r.Object.Catalog == command.Catalog)
                        && schemaFilter(r.Object.SchemaName ?? "") && tableFilter(r.Object.Name)
                        && (wantedTypes is null || wantedTypes.Contains(r.Type)))
            .OrderBy(r => r.Object.Catalog, StringComparer.Ordinal).ThenBy(r => r.Object.SchemaName, StringComparer.Ordinal).ThenBy(r => r.Object.Name, StringComparer.Ordinal)
            .ToList();

        var arrays = new List<IArrowArray>
        {
            Strings(tables.Select(r => r.Object.Catalog)), Strings(tables.Select(r => r.Object.SchemaName)), Strings(tables.Select(r => (string?)r.Object.Name)), Strings(tables.Select(r => (string?)r.Type)),
        };
        if (command.IncludeSchema)
        {
            var builder = new BinaryArray.Builder();
            foreach (var r in tables) builder.Append((ReadOnlySpan<byte>)SchemaExtensions.SerializeSchema(r.Object.Columns ?? new Schema([], null)));
            arrays.Add(builder.Build());
        }
        await WriteAsync(writer, GetTableSchema(command.IncludeSchema), arrays, tables.Count).ConfigureAwait(false);
    }

    protected override async Task DoGetTableType(CommandGetTableTypes command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        _ = context.GetServingContext();
        await WriteAsync(writer, TableTypesSchema, [Strings(TableTypes)], TableTypes.Length).ConfigureAwait(false);
    }

    protected override async Task DoGetPrimaryKeys(CommandGetPrimaryKeys command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var table = (await ObjectsAsync(context).ConfigureAwait(false)).Where(IsTable)
            .FirstOrDefault(o => o.Name == command.Table && (!command.HasCatalog || o.Catalog == command.Catalog) && (!command.HasDbSchema || o.SchemaName == command.DbSchema));
        var keys = table?.PrimaryKey ?? [];

        var sequence = new Int32Array.Builder();
        for (int i = 0; i < keys.Count; i++) sequence.Append(i + 1);
        await WriteAsync(writer, PrimaryKeysSchema,
        [
            Strings(keys.Select(_ => table!.Catalog)), Strings(keys.Select(_ => table!.SchemaName)), Strings(keys.Select(_ => (string?)table!.Name)),
            Strings(keys.Select(k => (string?)k)), Strings(keys.Select(_ => (string?)null)), sequence.Build(),
        ], keys.Count).ConfigureAwait(false);
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

    // GetSqlInfo is per data source: the x-datasource header picks the backend whose product, version, quoting and collation-related facts are reported. With no data source selected (several are configured and the client has not
    // said which) it answers for the server alone.
    protected override async Task DoGetSqlInfo(CommandGetSqlInfo command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context)
    {
        var serving = context.GetServingContext();
        string requestedSource = context.RequestedSource();
        ServingSourceFacts? source = null;
        try { source = await Services.Catalog.FactsAsync(serving, requestedSource, context.CancellationToken).ConfigureAwait(false); }
        catch (ServingInvalidException) when (requestedSource.Length == 0)
        {
            // Nothing selected and nothing to default to.
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw Fail(ex); }

        var facts = Describe(source);
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

    private SqlInfoFacts Describe(ServingSourceFacts? source)
    {
        var facts = new SqlInfoFacts();
        facts.Strings[(uint)SqlInfo.FlightSqlServerName] = Options.ServerName;
        facts.Strings[(uint)SqlInfo.FlightSqlServerVersion] = Options.ServerVersion;
        facts.Strings[(uint)SqlInfo.FlightSqlServerArrowVersion] = typeof(Schema).Assembly.GetName().Version?.ToString(3) ?? "";
        facts.Bools[(uint)SqlInfo.FlightSqlServerReadOnly] = false;
        facts.Bools[(uint)SqlInfo.FlightSqlServerSql] = true;
        facts.Bools[(uint)SqlInfo.FlightSqlServerSubstrait] = false;
        facts.Bools[(uint)SqlInfo.FlightSqlServerCancel] = false;
        facts.Bools[(uint)SqlInfo.FlightSqlServerBulkIngestion] = false;
        if (source is null) return facts;

        // Read-only means no statement can change data through this data source's update path; queries are still limited only by the database account, so use a read-only login for real protection.
        facts.Bools[(uint)SqlInfo.FlightSqlServerReadOnly] = source.ReadOnly;
        if (source.Product.Length > 0) facts.Strings[(uint)SqlInfo.FlightSqlServerName] = source.Product;
        if (source.Version is { Length: > 0 } version) facts.Strings[(uint)SqlInfo.FlightSqlServerVersion] = version;
        if (source.IdentifiersCaseInsensitive == true)
        {
            facts.Longs[(uint)SqlInfo.SqlIdentifierCase] = CaseInsensitive;
            facts.Longs[(uint)SqlInfo.SqlQuotedIdentifierCase] = CaseInsensitive;
        }
        if (source.NullsSortLow is { } low) facts.Longs[(uint)SqlInfo.SqlNullOrdering] = low ? NullsSortedLow : NullsSortedAtEnd;
        if (source.IdentifierQuote is not null) facts.Strings[(uint)SqlInfo.SqlIdentifierQuoteChar] = source.IdentifierQuote;
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

    /// <summary>
    /// SQL LIKE patterns as a case-insensitive matcher; null matches everything. The shared matcher is linear (a hostile pattern cannot make it backtrack, which the regular expression this replaces could) and honours the backslash
    /// escape of the ODBC and JDBC catalog functions (<c>\_</c> is a literal underscore).
    /// </summary>
    private static Func<string, bool> Pattern(string? like) => LikePattern.Compile(like);
}
