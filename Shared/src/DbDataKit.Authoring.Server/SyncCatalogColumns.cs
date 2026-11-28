using DbDataKit.Catalog;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Provisioning;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The columns of a sync draft's tables as the draft's <c>catalog-cache.yaml</c> holds them, as the catalog Sync's own column reader
/// (<see cref="MappingColumnReader"/>) asks. With it, a mapping's columns, the types inferred from them and the key derived from them are read
/// in the Workspace's process, by the same code the engine runs, without a connection to any database: the cache is a picture taken by a read
/// on a Runner, and nothing here reads a database.
/// <para>
/// A table matches on its schema and name, ignoring case; the database matches when both name one, so a cache read with no database
/// (an engine that has none) still answers. The cache marks key columns but keeps no key order, so <see cref="ColumnMetadata.KeyOrdinal"/>
/// is 0 and a key is read in column order. A table the cache does not hold answers no columns, which Sync's reader reports as a table
/// that is not there; a connection the cache does not hold is an error the reader reports as the reason it could not look.
/// </para>
/// </summary>
public sealed class SyncCatalogColumns(CatalogCacheFile cache) : IColumnCatalog
{
    /// <summary>The draft's catalog cache, or null when the draft has none or it does not read (the draft's check reports why).</summary>
    public static SyncCatalogColumns? Read(string draftRoot)
    {
        var path = Path.Combine(draftRoot, CatalogCache.FileName);
        if (!File.Exists(path)) return null;
        var load = CatalogCache.Load(File.ReadAllText(path), CatalogCache.FileName);
        return load.Ok && load.Cache is { } file ? new SyncCatalogColumns(file) : null;
    }

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
    {
        if (!cache.Connections.TryGetValue(connectionName, out var connection))
            throw new InvalidOperationException($"The connection '{connectionName}' has no entry in {CatalogCache.FileName}: refresh its catalog.");

        var found = connection.Tables.FirstOrDefault(t =>
            string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)
            && (database.Length == 0 || t.Database.Length == 0 || string.Equals(t.Database, database, StringComparison.OrdinalIgnoreCase)));

        IReadOnlyList<ColumnMetadata> columns = found is null
            ? []
            : [.. found.Columns.Select(c => new ColumnMetadata(c.Name, c.Type, c.Nullable, c.Key, IsIdentity: false))];
        return Task.FromResult(columns);
    }
}
