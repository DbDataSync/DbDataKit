namespace DbDataKit.Metadata;

/// <summary>The fallback of <see cref="ICatalogReader.ReadSchemaKeysAsync"/>: the keys of a schema read table by table, from each table's shape. A reader that has a schema-wide query does not use it.</summary>
public static class SchemaKeysReader
{
    public static async Task<SchemaKeys> PerTableAsync(ICatalogReader reader, string? catalog, string schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(schema);
        var primaryKeys = new Dictionary<string, KeyInfo>(StringComparer.Ordinal);
        var foreignKeys = new Dictionary<string, IReadOnlyList<ForeignKeyInfo>>(StringComparer.Ordinal);
        bool pkRead = true, fkRead = true;
        var warnings = new List<string>();
        var filter = new TableFilter(catalog, LikePattern.Escape(schema), null, new HashSet<TableKind> { TableKind.Table });
        var tables = new List<TableRef>();
        await foreach (var t in reader.ListTablesAsync(filter, cancellationToken).ConfigureAwait(false)) tables.Add(t);
        foreach (var t in tables)
        {
            TableShape shape;
            try { shape = await reader.GetShapeAsync(t, cancellationToken).ConfigureAwait(false); }
            catch (KeyNotFoundException) { continue; }       // listed, but its columns are not visible to this login
            pkRead &= shape.Support.PrimaryKeys;
            fkRead &= shape.Support.ForeignKeys;
            if (shape.PrimaryKey is not null) primaryKeys[t.Name] = shape.PrimaryKey;
            if (shape.ForeignKeys.Count > 0) foreignKeys[t.Name] = shape.ForeignKeys;
            foreach (var w in shape.WarningList) if (!warnings.Contains(w)) warnings.Add(w);
        }
        return new SchemaKeys(primaryKeys, foreignKeys, pkRead, fkRead, warnings.Count == 0 ? null : warnings);
    }
}
