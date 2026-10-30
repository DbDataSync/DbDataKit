using DbDataKit.Connections.FlightSql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// Catalog browsing in the terms of Sync's drivers, over the shared <see cref="FlightSqlMetadataReader"/> (which makes the calls: <c>GetCatalogs</c>, <c>GetTables</c>, <c>GetPrimaryKeys</c>). Each is optional in
/// the protocol, and a proxy often implements none of them, so a call the server does not offer is "this server has no catalog to browse" (an empty list, which the mapping editor answers by offering the query
/// editor instead), not an error.
/// </summary>
internal static class FlightSqlMetadata
{
    public static async Task<IReadOnlyList<string>> CatalogsAsync(FlightSqlConnection connection, CancellationToken ct) =>
        await new FlightSqlMetadataReader(connection).CatalogsAsync(null, ct) ?? [];

    public static async Task<IReadOnlyList<TableMetadata>> TablesAsync(FlightSqlConnection connection, string database, CancellationToken ct)
    {
        var rows = await new FlightSqlMetadataReader(connection).TablesAsync(database, null, null, includeSchema: false, null, ct) ?? [];
        return rows.Where(r => !IsSystem(r.Type)).Select(r => new TableMetadata(r.Schema ?? "", r.Name ?? "")).ToList();
    }

    public static async Task<IReadOnlyList<ColumnMetadata>> ColumnsAsync(
        FlightSqlConnection connection, string database, string schema, string table, CancellationToken ct)
    {
        var reader = new FlightSqlMetadataReader(connection);
        foreach (var row in await reader.TablesAsync(database, schema, table, includeSchema: true, null, ct) ?? [])
        {
            // The filters are LIKE patterns (`_` matches any character), so an exact match is re-checked.
            if (!string.Equals(row.Name ?? "", table, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(schema) && !string.Equals(row.Schema ?? "", schema, StringComparison.OrdinalIgnoreCase))
                || row.ReadSchema() is not { } arrowSchema)
                continue;

            // Key metadata is the most commonly unimplemented call; "no keys" is a valid answer and any other failure still surfaces.
            var keys = (await reader.PrimaryKeysAsync(database, row.Schema ?? "", row.Name ?? "", null, ct) ?? [])
                .Select(k => k.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return arrowSchema.FieldsList
                .Select(f => new ColumnMetadata(f.Name, ArrowValues.NativeTypeName(f.DataType), f.IsNullable, keys.Contains(f.Name), IsIdentity: false))
                .ToList();
        }

        return [];
    }

    private static bool IsSystem(string type) =>
        type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)
        || type.Contains("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);
}
