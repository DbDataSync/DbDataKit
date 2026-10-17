using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using DbDataSync.Drivers.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// Catalog browsing through Flight SQL's metadata calls (<c>GetCatalogs</c>, <c>GetTables</c>,
/// <c>GetPrimaryKeys</c>). Each is optional in the protocol, and a proxy often implements none of them —
/// so an <c>Unimplemented</c> answer is "this server has no catalog to browse" (an empty list, which the
/// mapping editor answers by offering the query editor instead), not an error.
/// </summary>
internal static class FlightSqlMetadata
{
    public static async Task<IReadOnlyList<string>> CatalogsAsync(FlightSqlConnection connection, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var batch in ReadAsync(connection, async options => [await connection.Client.GetCatalogsAsync(options, ct)], ct))
        {
            var column = Column(batch, "catalog_name");
            for (var i = 0; i < batch.Length; i++)
            {
                if (ArrowValues.ValueAt(column, i) is string name)
                    names.Add(name);
            }
        }

        return names;
    }

    public static async Task<IReadOnlyList<TableMetadata>> TablesAsync(
        FlightSqlConnection connection, string database, CancellationToken ct)
    {
        var tables = new List<TableMetadata>();
        await foreach (var row in TableRowsAsync(connection, database, schema: null, table: null, includeSchema: false, ct))
        {
            if (IsSystem(row.Type))
                continue;

            tables.Add(new TableMetadata(row.Schema, row.Table));
        }

        return tables;
    }

    public static async Task<IReadOnlyList<ColumnMetadata>> ColumnsAsync(
        FlightSqlConnection connection, string database, string schema, string table, CancellationToken ct)
    {
        await foreach (var row in TableRowsAsync(connection, database, schema, table, includeSchema: true, ct))
        {
            // The filters are LIKE patterns (`_` matches any character), so an exact match is re-checked.
            if (!string.Equals(row.Table, table, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(schema) && !string.Equals(row.Schema, schema, StringComparison.OrdinalIgnoreCase))
                || row.SchemaBytes is null)
                continue;

            var keys = await PrimaryKeysAsync(connection, database, row.Schema, row.Table, ct);
            return ArrowSerializationHelpers.DeserializeSchema(row.SchemaBytes).FieldsList
                .Select(f => new ColumnMetadata(
                    f.Name, ArrowValues.NativeTypeName(f.DataType), f.IsNullable, keys.Contains(f.Name), IsIdentity: false))
                .ToList();
        }

        return [];
    }

    private static async Task<HashSet<string>> PrimaryKeysAsync(
        FlightSqlConnection connection, string database, string schema, string table, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var command = new CommandGetPrimaryKeys { Table = table };
        if (!string.IsNullOrEmpty(database)) command.Catalog = database;
        if (!string.IsNullOrEmpty(schema)) command.DbSchema = schema;

        // Key metadata is the most commonly unimplemented call; ReadAsync turns that into an empty
        // result, so "no keys" is a valid answer and any *other* failure still surfaces.
        await foreach (var batch in ReadAsync(connection, options => InfoAsync(connection, command, options, ct), ct))
        {
            var column = Column(batch, "column_name");
            for (var i = 0; i < batch.Length; i++)
            {
                if (ArrowValues.ValueAt(column, i) is string name)
                    keys.Add(name);
            }
        }

        return keys;
    }

    private sealed record TableRow(string Schema, string Table, string Type, byte[]? SchemaBytes);

    private static async IAsyncEnumerable<TableRow> TableRowsAsync(
        FlightSqlConnection connection, string? database, string? schema, string? table, bool includeSchema,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // The command is built here rather than through FlightSqlClient.GetTablesAsync, which turns an
        // omitted filter into an empty string — and an empty catalog means "tables with no catalog" and an
        // empty pattern matches nothing, so an unfiltered browse would come back empty. A field left
        // unset is the protocol's way of saying "no filter".
        var command = new CommandGetTables { IncludeSchema = includeSchema };
        if (!string.IsNullOrEmpty(database)) command.Catalog = database;
        if (!string.IsNullOrEmpty(schema)) command.DbSchemaFilterPattern = schema;
        if (!string.IsNullOrEmpty(table)) command.TableNameFilterPattern = table;

        await foreach (var batch in ReadAsync(connection, options => InfoAsync(connection, command, options, ct), ct))
        {
            var schemas = Column(batch, "db_schema_name");
            var tables = Column(batch, "table_name");
            var types = Column(batch, "table_type");
            var tableSchemas = includeSchema ? Column(batch, "table_schema") : null;

            for (var i = 0; i < batch.Length; i++)
            {
                yield return new TableRow(
                    ArrowValues.ValueAt(schemas, i) as string ?? "",
                    ArrowValues.ValueAt(tables, i) as string ?? "",
                    ArrowValues.ValueAt(types, i) as string ?? "",
                    tableSchemas is null ? null : ArrowValues.ValueAt(tableSchemas, i) as byte[]);
            }
        }
    }

    private static async Task<List<FlightInfo>> InfoAsync(
        FlightSqlConnection connection, IMessage command, FlightCallOptions options, CancellationToken ct)
    {
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Any.Pack(command).ToByteArray());
        return [await connection.Client.GetFlightInfoAsync(descriptor, options, ct)];
    }

    /// <summary>Runs a metadata call and streams every endpoint of every <see cref="FlightInfo"/> it
    /// returns. An <c>Unimplemented</c> reply ends the sequence quietly.</summary>
    private static async IAsyncEnumerable<RecordBatch> ReadAsync(
        FlightSqlConnection connection, Func<FlightCallOptions, Task<List<FlightInfo>>> call,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var options = connection.CallOptions();

        List<FlightInfo>? infos = null;
        var unimplemented = false;
        try
        {
            infos = await call(options);
        }
        catch (Exception ex) when (FlightSqlException.TryWrap(ex, out var wrapped))
        {
            if (wrapped.StatusCode != StatusCode.Unimplemented)
                throw wrapped;

            unimplemented = true;
        }

        if (unimplemented || infos is null)
            yield break;

        foreach (var info in infos)
        {
            foreach (var endpoint in info.Endpoints)
            {
                var batches = connection.DoGetAsync(endpoint.Ticket, options, ct).GetAsyncEnumerator(ct);
                await using (batches)
                {
                    while (true)
                    {
                        try
                        {
                            if (!await batches.MoveNextAsync())
                                break;
                        }
                        catch (Exception ex) when (FlightSqlException.TryWrap(ex, out var wrapped))
                        {
                            throw wrapped;
                        }

                        yield return batches.Current;
                    }
                }
            }
        }
    }

    private static IArrowArray Column(RecordBatch batch, string name) =>
        batch.Column(name) ?? throw new InvalidOperationException(
            $"The server's metadata answer has no '{name}' column, which the Flight SQL specification requires.");

    private static bool IsSystem(string type) =>
        type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)
        || type.Contains("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);
}
