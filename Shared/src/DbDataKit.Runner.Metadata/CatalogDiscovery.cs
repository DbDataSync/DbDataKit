using DbDataKit.Catalog;
using DbDataKit.Metadata;

namespace DbDataKit.Runner.Metadata;

/// <summary>What <see cref="CatalogDiscovery.ReadAsync"/> reads: the schemas given (none: every schema of the connection), a name pattern for the tables (SQL <c>LIKE</c>: <c>%</c> and <c>_</c>), or one table of one schema.</summary>
public sealed record DiscoverScope(IReadOnlyList<string>? Schemas = null, string? Pattern = null, string? Table = null)
{
    /// <summary>Why the scope cannot be read, or null when it can: one table needs exactly one schema, and a pattern does not go with one table.</summary>
    public string? Problem() =>
        Table is { Length: > 0 } && (Schemas is not { Count: 1 }) ? $"The table '{Table}' is read in one schema: give exactly one schema with it."
        : Table is { Length: > 0 } && Pattern is { Length: > 0 } ? "Give a table or a pattern, not both."
        : null;
}

/// <summary>
/// The shape of a connection as a connection of the catalog cache (<c>catalog-cache/2</c>): the databases it reaches with every schema of each (the empty ones too), and the tables and views of the schemas in scope with their columns (the type
/// text the database gives, nullability, the primary key), unique keys and foreign keys. It reads the catalog only: no row, no value and no statistic of values is read. The answer's <c>read</c> is left out: the caller knows which server and
/// channel it asked.
/// Design: docs/authoring-metadata.md#the-catalog-cache
/// </summary>
public static class CatalogDiscovery
{
    /// <summary>Schemas an engine keeps for itself, never offered as a place for a project's tables.</summary>
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "pg_catalog", "pg_toast", "sys", "guest", "mysql", "performance_schema",
        "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator", "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter",
        "SYSTEM", "SYSAUX", "OUTLN", "XDB", "CTXSYS", "MDSYS", "ORDSYS", "ORDDATA", "OLAPSYS", "WMSYS", "DBSNMP", "APPQOSSYS", "AUDSYS", "GSMADMIN_INTERNAL", "LBACSYS", "DVSYS", "OJVMSYS", "DBSFWUSER", "REMOTE_SCHEDULER_AGENT", "ANONYMOUS",
        "XS$NULL", "GSMCATUSER", "GSMUSER", "SYSBACKUP", "SYSDG", "SYSKM", "SYSRAC", "DIP", "MDDATA", "ORACLE_OCM", "SI_INFORMTN_SCHEMA", "GGSYS", "PDBADMIN",
    };

    public static bool IsSystemSchema(string schema) => SystemSchemas.Contains(schema) || schema.StartsWith("pg_temp_", StringComparison.Ordinal) || schema.StartsWith("pg_toast_temp_", StringComparison.Ordinal);

    public static async Task<CacheConnection> ReadAsync(ICatalogReader reader, string engine, DiscoverScope scope, CancellationToken ct)
    {
        if (scope.Problem() is { } problem) throw new ArgumentException(problem);

        // every schema of the connection, by database, the empty ones too
        var byDatabase = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var all = new List<SchemaRef>();
        await foreach (var schema in reader.ListSchemasAsync(ct).ConfigureAwait(false))
        {
            if (IsSystemSchema(schema.Name)) continue;
            all.Add(schema);
            if (schema.Catalog is { Length: > 0 } database)
            {
                if (!byDatabase.TryGetValue(database, out var names)) byDatabase[database] = names = new SortedSet<string>(StringComparer.Ordinal);
                names.Add(schema.Name);
            }
        }

        var wanted = scope.Schemas is { Count: > 0 } named ? new HashSet<string>(named, StringComparer.Ordinal) : null;
        var nameFilter = scope.Pattern is { Length: > 0 } pattern ? new TableFilter(NamePattern: pattern).Compile() : null;
        var tables = new List<CacheTable>();
        foreach (var schema in all.Where(s => wanted is null || wanted.Contains(s.Name)))
        {
            ct.ThrowIfCancellationRequested();
            var shapes = await reader.ReadSchemaAsync(null, schema.Name, scope.Table is { Length: > 0 } table ? table : null, ct).ConfigureAwait(false);
            shapes = [.. shapes.Where(s => s.Table.Kind is TableKind.Table or TableKind.View && (nameFilter is null || nameFilter(s.Table)))];
            if (shapes.Count == 0) continue;
            var keys = await reader.ReadSchemaKeysAsync(null, schema.Name, ct).ConfigureAwait(false);
            foreach (var shape in shapes)
                if (TableOf(shape, keys, byDatabase.Count > 0 ? schema.Catalog ?? "" : "") is { } cached) tables.Add(cached);
        }

        return new CacheConnection(engine, tables) { Databases = [.. byDatabase.Select(d => new CacheDatabase(d.Key, [.. d.Value]))] };
    }

    /// <summary>One table of the answer; null for one with no column the reader could read (the cache holds no table without columns).</summary>
    internal static CacheTable? TableOf(TableShape shape, SchemaKeys keys, string database)
    {
        if (shape.Columns.Count == 0) return null;
        var name = shape.Table.Name;
        var primary = shape.PrimaryKey ?? keys.PrimaryKeys.GetValueOrDefault(name);
        var keyColumns = new HashSet<string>(primary?.Columns ?? [], StringComparer.Ordinal);
        var columns = shape.Columns.OrderBy(c => c.Ordinal).Select(c => new CacheColumn(c.Name, c.NativeType, c.Nullable, keyColumns.Contains(c.Name))).ToList();

        // a unique index is a unique key, except the one behind the primary key
        var unique = shape.Indexes.Where(i => i.Unique && i.Columns.Count > 0)
            .Select(i => new CacheKey(i.Name, [.. i.Columns.Select(c => c.Name)]))
            .Where(k => !(primary is not null && k.Columns.SequenceEqual(primary.Columns, StringComparer.Ordinal)))
            .DistinctBy(k => string.Join('\0', k.Columns), StringComparer.Ordinal)
            .ToList();

        var foreign = (shape.ForeignKeys.Count > 0 ? shape.ForeignKeys : keys.ForeignKeys.GetValueOrDefault(name) ?? [])
            .Where(f => f.Columns.Count > 0 && f.Columns.Count == f.ReferencedColumns.Count)
            .Select(f => new CacheForeignKey(f.Name, f.Columns, f.Referenced.Catalog is { } other && other != database && database.Length > 0 ? other : "", f.Referenced.Schema ?? "", f.Referenced.Name, f.ReferencedColumns))
            .ToList();

        return new CacheTable(shape.Table.Schema ?? "", name, shape.Table.Kind == TableKind.View ? "view" : "table", columns)
        {
            Database = database,
            UniqueKeys = unique,
            ForeignKeys = foreign,
        };
    }
}
