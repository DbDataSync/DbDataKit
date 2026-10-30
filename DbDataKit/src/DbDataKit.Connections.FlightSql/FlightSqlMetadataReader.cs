using Apache.Arrow;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight;
using Arrow.Flight.Protocol.Sql;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DbDataKit.Connections.FlightSql;

/// <summary>A table (or view) as `CommandGetTables` describes it. <see cref="SchemaBytes"/> is the serialized Arrow schema of its columns, present only when the call asked for it and the server sent one.</summary>
public sealed record FlightSqlTableInfo(string? Catalog, string? Schema, string? Name, string Type, byte[]? SchemaBytes)
{
    /// <summary>The columns of the table as an Arrow schema; null when the server sent none.</summary>
    public Schema? ReadSchema() => SchemaBytes is null ? null : SchemaExtensions.DeserializeSchema(new ReadOnlyMemory<byte>(SchemaBytes));
}

/// <summary>One column of a table's primary key, with its place in the key (<see cref="Sequence"/> is null when the server gave none).</summary>
public sealed record FlightSqlKeyColumn(string Column, int? Sequence);

/// <summary>One column of a foreign key as `CommandGetImportedKeys` describes it.</summary>
public sealed record FlightSqlImportedKey(string? KeyName, int? Sequence, string Column, string? ReferencedSchema, string ReferencedTable, string ReferencedColumn);

/// <summary>
/// Flight SQL's catalog calls on a <see cref="FlightSqlConnection"/> (<c>GetCatalogs</c>, <c>GetDbSchemas</c>, <c>GetTables</c>, <c>GetPrimaryKeys</c>, <c>GetImportedKeys</c>, <c>GetSqlInfo</c>), each read to the end
/// and handed over as plain records. The commands are built here, not through the client library's metadata methods, which send an empty string for a filter left out (<c>catalog = ""</c>), and servers read that as
/// "the catalog with no name" and answer with nothing: a field left unset is the protocol's way of saying "no filter".
/// Every call is optional in the protocol, and a proxy often implements none of them, so each answer is <b>null</b> when the server does not offer the call (gRPC <c>Unimplemented</c>; and
/// <c>InvalidArgument</c> too when <c>invalidArgumentIsNotOffered</c> is set, which is how a server that rejects a metadata command it does not know says so). Any other failure is a <see cref="FlightSqlException"/>.
/// </summary>
public sealed class FlightSqlMetadataReader(FlightSqlConnection connection, bool invalidArgumentIsNotOffered = false)
{
    public async Task<IReadOnlyList<string>?> CatalogsAsync(TimeSpan? timeout, CancellationToken ct) =>
        await RunAsync(new CommandGetCatalogs(), timeout, batches => Strings(batches, "catalog_name"), ct);

    /// <summary>The schema names the server offers (in <paramref name="catalog"/>, when given), in the server's order.</summary>
    public async Task<IReadOnlyList<string>?> SchemasAsync(string? catalog, TimeSpan? timeout, CancellationToken ct)
    {
        var command = new CommandGetDbSchemas();
        if (!string.IsNullOrEmpty(catalog)) command.Catalog = catalog;
        return await RunAsync(command, timeout, batches => Strings(batches, "db_schema_name"), ct);
    }

    /// <summary>
    /// The tables of a catalog and schema pattern. The patterns are LIKE patterns (<c>_</c> matches any character), so a caller that wants an exact name compares it again. Rows come back as the server sent them.
    /// </summary>
    public async Task<IReadOnlyList<FlightSqlTableInfo>?> TablesAsync(
        string? catalog, string? schemaPattern, string? tablePattern, bool includeSchema, TimeSpan? timeout, CancellationToken ct)
    {
        var command = new CommandGetTables { IncludeSchema = includeSchema };
        if (!string.IsNullOrEmpty(catalog)) command.Catalog = catalog;
        if (!string.IsNullOrEmpty(schemaPattern)) command.DbSchemaFilterPattern = schemaPattern;
        if (!string.IsNullOrEmpty(tablePattern)) command.TableNameFilterPattern = tablePattern;
        return await RunAsync(command, timeout, batches =>
        {
            var found = new List<FlightSqlTableInfo>();
            foreach (var batch in batches)
                for (var r = 0; r < batch.Length; r++)
                    found.Add(new(Text(batch, "catalog_name", r), Text(batch, "db_schema_name", r), Text(batch, "table_name", r), Text(batch, "table_type", r) ?? "",
                        includeSchema && Column(batch, "table_schema") is { } schemas ? ArrowValueReader.Value(schemas, r) as byte[] : null));
            return (IReadOnlyList<FlightSqlTableInfo>)found;
        }, ct);
    }

    public async Task<IReadOnlyList<FlightSqlKeyColumn>?> PrimaryKeysAsync(string? catalog, string? schema, string table, TimeSpan? timeout, CancellationToken ct)
    {
        var command = new CommandGetPrimaryKeys { Table = table };
        if (!string.IsNullOrEmpty(catalog)) command.Catalog = catalog;
        if (!string.IsNullOrEmpty(schema)) command.DbSchema = schema;
        return await RunAsync(command, timeout, batches =>
        {
            var columns = new List<FlightSqlKeyColumn>();
            foreach (var batch in batches)
                for (var r = 0; r < batch.Length; r++)
                    if (Text(batch, "column_name", r) is { } column) columns.Add(new(column, Int(batch, "key_sequence", r)));
            return (IReadOnlyList<FlightSqlKeyColumn>)columns;
        }, ct);
    }

    public async Task<IReadOnlyList<FlightSqlImportedKey>?> ImportedKeysAsync(string? catalog, string? schema, string table, TimeSpan? timeout, CancellationToken ct)
    {
        var command = new CommandGetImportedKeys { Table = table };
        if (!string.IsNullOrEmpty(catalog)) command.Catalog = catalog;
        if (!string.IsNullOrEmpty(schema)) command.DbSchema = schema;
        return await RunAsync(command, timeout, batches =>
        {
            var keys = new List<FlightSqlImportedKey>();
            foreach (var batch in batches)
                for (var r = 0; r < batch.Length; r++)
                    if (Text(batch, "fk_column_name", r) is { } column && Text(batch, "pk_table_name", r) is { } refTable && Text(batch, "pk_column_name", r) is { } refColumn)
                        keys.Add(new(Text(batch, "fk_key_name", r), Int(batch, "key_sequence", r), column, Text(batch, "pk_db_schema_name", r), refTable, refColumn));
            return (IReadOnlyList<FlightSqlImportedKey>)keys;
        }, ct);
    }

    /// <summary>
    /// <c>GetSqlInfo</c> for the ids given: what the server says it is. The value of an id is the string, boolean, 32- or 64-bit number the protocol's union carries for it (as <see cref="string"/>,
    /// <see cref="bool"/>, <see cref="int"/>, <see cref="long"/>); an id whose value is of another kind is left out rather than guessed at.
    /// </summary>
    public async Task<IReadOnlyDictionary<uint, object>?> SqlInfoAsync(IEnumerable<uint> ids, TimeSpan? timeout, CancellationToken ct)
    {
        var command = new CommandGetSqlInfo();
        foreach (var id in ids) command.Info.Add(id);
        return await RunAsync(command, timeout, batches =>
        {
            var found = new Dictionary<uint, object>();
            foreach (var batch in batches)
            {
                if (batch.ColumnCount < 2 || batch.Column(0) is not UInt32Array idColumn || batch.Column(1) is not DenseUnionArray values) continue;
                var typeIds = ((Apache.Arrow.Types.UnionType)values.Data.DataType).TypeIds.ToArray();
                for (var row = 0; row < batch.Length; row++)
                {
                    if (idColumn.GetValue(row) is not { } id) continue;
                    var child = System.Array.IndexOf(typeIds, (int)values.TypeIds[row]);
                    if (child < 0) continue;
                    var offset = values.ValueOffsets[row];
                    object? value = values.Fields[child] switch
                    {
                        StringArray strings => strings.GetString(offset),
                        BooleanArray flags => flags.GetValue(offset),
                        Int64Array longs => longs.GetValue(offset),
                        Int32Array ints => ints.GetValue(offset),
                        _ => null,
                    };
                    if (value is not null) found[id] = value;
                }
            }
            return (IReadOnlyDictionary<uint, object>)found;
        }, ct);
    }

    private async Task<T?> RunAsync<T>(IMessage command, TimeSpan? timeout, Func<List<RecordBatch>, T> read, CancellationToken ct) where T : class
    {
        try
        {
            var options = connection.CallOptions(timeout);
            var info = await connection.Client.GetFlightInfoAsync(FlightDescriptor.CreateCommandDescriptor(Any.Pack(command).ToByteArray()), options, ct);
            var batches = new List<RecordBatch>();
            foreach (var endpoint in info.Endpoints)
                await foreach (var batch in connection.DoGetAsync(endpoint.Ticket, options, ct)) batches.Add(batch);
            return read(batches);
        }
        catch (Exception ex) when (FlightSqlException.TryWrap(ex, out var wrapped))
        {
            if (wrapped.StatusCode == StatusCode.Unimplemented || (invalidArgumentIsNotOffered && wrapped.StatusCode == StatusCode.InvalidArgument)) return null;
            throw wrapped;
        }
    }

    private static IReadOnlyList<string> Strings(List<RecordBatch> batches, string column)
    {
        var names = new List<string>();
        foreach (var batch in batches)
        {
            var array = Column(batch, column) ?? throw new InvalidOperationException($"The server's metadata answer has no '{column}' column, which the Flight SQL specification requires.");
            for (var i = 0; i < batch.Length; i++) if (ArrowValueReader.Value(array, i) is string name) names.Add(name);
        }
        return names;
    }

    private static IArrowArray? Column(RecordBatch batch, string name) => batch.Schema.GetFieldIndex(name) is var i and >= 0 ? batch.Column(i) : null;

    private static string? Text(RecordBatch batch, string name, int row) => Column(batch, name) is { } array ? ArrowValueReader.Value(array, row)?.ToString() : null;

    private static int? Int(RecordBatch batch, string name, int row) => int.TryParse(Text(batch, name, row), out var value) ? value : null;
}
