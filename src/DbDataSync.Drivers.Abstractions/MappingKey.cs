using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Applies a mapping's stated <see cref="TableMappingConfig.PrimaryKey"/> over the source's own
/// description of its columns — phase 201M. One place, so a preview and a run read the same key.
/// <para>
/// With a stated key, exactly those columns are keys (the source's own answer is replaced, not merged)
/// and each is non-nullable: a key column can't hold NULL, and a source that reports every column
/// nullable would otherwise produce a table whose <c>PRIMARY KEY</c> the engine refuses. With none
/// stated the columns come back untouched.
/// </para>
/// </summary>
public static class MappingKey
{
    public static IReadOnlyList<ColumnMetadata> WithKey(
        this IReadOnlyList<ColumnMetadata> columns, IReadOnlyList<string> key)
    {
        if (key.Count == 0)
            return columns;

        var keys = new HashSet<string>(key, StringComparer.OrdinalIgnoreCase);
        return columns
            .Select(c => keys.Contains(c.Name)
                ? c with { IsPrimaryKey = true, IsNullable = false }
                : c with { IsPrimaryKey = false })
            .ToList();
    }

    public static IReadOnlyList<CachedColumn> WithKey(
        this IReadOnlyList<CachedColumn> columns, IReadOnlyList<string> key)
    {
        if (key.Count == 0)
            return columns;

        var keys = new HashSet<string>(key, StringComparer.OrdinalIgnoreCase);
        return columns
            .Select(c => keys.Contains(c.Name)
                ? new CachedColumn(c.Name, c.NativeType, isNullable: false, isPrimaryKey: true, c.IsIdentity)
                : new CachedColumn(c.Name, c.NativeType, c.IsNullable, isPrimaryKey: false, c.IsIdentity))
            .ToList();
    }
}
