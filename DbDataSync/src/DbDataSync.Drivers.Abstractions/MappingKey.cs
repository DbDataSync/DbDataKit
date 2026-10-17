using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Applies a mapping's stated <see cref="TableMappingConfig.PrimaryKey"/> over the source's own
/// description of its columns — phase 201M. One place, so a preview and a run read the same key.
/// <para>
/// With a stated key, exactly those columns are keys, in the order stated (the source's own answer is replaced, not merged)
/// and each is non-nullable: a key column can't hold NULL, and a source that reports every column
/// nullable would otherwise produce a table whose <c>PRIMARY KEY</c> the engine refuses. With none
/// stated the columns come back untouched.
/// </para>
/// </summary>
public static class MappingKey
{
    /// <summary>The key columns in key order: known positions first (by ordinal), then any whose
    /// position is unknown in column order. The order of a primary key is the order of its index, so
    /// everything that lists a key goes through here rather than filtering on <c>IsPrimaryKey</c>.</summary>
    public static IEnumerable<ColumnMetadata> InKeyOrder(this IEnumerable<ColumnMetadata> columns) =>
        columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.KeyOrdinal == 0 ? int.MaxValue : c.KeyOrdinal);

    public static IEnumerable<CachedColumn> InKeyOrder(this IEnumerable<CachedColumn> columns) =>
        columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.KeyOrdinal == 0 ? int.MaxValue : c.KeyOrdinal);

    private static Dictionary<string, int> Ordinals(IReadOnlyList<string> key)
    {
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key)
            ordinals.TryAdd(name, ordinals.Count + 1);
        return ordinals;
    }

    public static IReadOnlyList<ColumnMetadata> WithKey(
        this IReadOnlyList<ColumnMetadata> columns, IReadOnlyList<string> key)
    {
        if (key.Count == 0)
            return columns;

        var ordinals = Ordinals(key);
        return columns
            .Select(c => ordinals.TryGetValue(c.Name, out var ordinal)
                ? c with { IsPrimaryKey = true, IsNullable = false, KeyOrdinal = ordinal }
                : c with { IsPrimaryKey = false, KeyOrdinal = 0 })
            .ToList();
    }

    public static IReadOnlyList<CachedColumn> WithKey(
        this IReadOnlyList<CachedColumn> columns, IReadOnlyList<string> key)
    {
        if (key.Count == 0)
            return columns;

        var ordinals = Ordinals(key);
        return columns
            .Select(c => ordinals.TryGetValue(c.Name, out var ordinal)
                ? new CachedColumn(c.Name, c.NativeType, isNullable: false, isPrimaryKey: true, c.IsIdentity, ordinal)
                : new CachedColumn(c.Name, c.NativeType, c.IsNullable, isPrimaryKey: false, c.IsIdentity))
            .ToList();
    }
}
