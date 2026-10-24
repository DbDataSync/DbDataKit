using System.Data.Common;
using DbDataKit.Metadata;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// Reads a table's columns through a shared <c>DbDataKit.Metadata</c> reader (plan 13, A11) and returns them in the shape Sync's drivers use. The reader runs on the
/// caller's connection and its command timeout is the one the connection was stamped with.
/// </summary>
public static class SharedCatalog
{
    public static CatalogReaderOptions OptionsFor(DbConnection connection) =>
        new() { CommandTimeout = TimeSpan.FromSeconds(ConnectionTimeouts.CommandTimeoutOf(connection)) };

    /// <summary>
    /// The columns of <paramref name="schema"/>.<paramref name="table"/>, with the primary key's members in key order. <paramref name="nativeType"/> and <paramref name="isIdentity"/>
    /// let an engine keep a rule of its own where Sync's meaning differs from the reader's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The table does not exist, or the login cannot see it.</exception>
    public static async Task<IReadOnlyList<ColumnMetadata>> ColumnsAsync(
        ICatalogReader reader, string schema, string table, CancellationToken cancellationToken,
        Func<ColumnInfo, string>? nativeType = null, Func<ColumnInfo, bool>? isIdentity = null)
    {
        TableShape shape;
        try { shape = await reader.GetShapeAsync(new TableRef(null, schema, table), cancellationToken).ConfigureAwait(false); }
        catch (KeyNotFoundException) { throw new InvalidOperationException($"Table '{schema}.{table}' was not found."); }

        var key = shape.PrimaryKey?.Columns ?? [];
        return [.. shape.Columns.Select(c =>
        {
            int ordinal = IndexOf(key, c.Name);
            return new ColumnMetadata(c.Name, nativeType?.Invoke(c) ?? c.NativeType, c.Nullable, ordinal > 0, isIdentity?.Invoke(c) ?? c.IsIdentity, ordinal);
        })];
    }

    private static int IndexOf(IReadOnlyList<string> key, string name)
    {
        for (int i = 0; i < key.Count; i++)
            if (string.Equals(key[i], name, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }
}
