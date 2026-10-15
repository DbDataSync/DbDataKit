using System.Data;
using System.Data.Common;
using DbData.Dialect;

namespace DbData.Metadata;

/// <summary>
/// Describing a query by asking the provider for its result schema. Every entry point checks the text with <see cref="ReadOnlyPolicy"/>
/// first: a describe must never be a way to run something that writes.
/// </summary>
public static class QueryProbe
{
    /// <summary>Throws <see cref="InvalidOperationException"/> unless the policy allows exactly one read-only statement; returns the text without trailing semicolons.</summary>
    public static string RequireReadOnly(string sql, ISqlDialect dialect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var decision = ReadOnlyPolicy.Check(sql, dialect);
        if (!decision.Allowed) throw new InvalidOperationException($"The read-only policy refused the query: {decision.Reason}");
        return TypeText.TrimStatement(sql) ?? throw new ArgumentException("the query is empty", nameof(sql));
    }

    /// <summary>
    /// The generic strategy: <c>SELECT * FROM (&lt;q&gt;) t WHERE 1 = 0</c> executed (it returns no rows) and the reader's schema read. Fails for
    /// queries that cannot be a derived table (a trailing ORDER BY on SQL Server, a WITH clause on some engines); engines with a real describe
    /// facility use it instead.
    /// </summary>
    public static async Task<QueryShape> WrappedZeroRowAsync(DbConnection connection, ISqlDialect dialect, string sql, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string inner = RequireReadOnly(sql, dialect);
        // Newlines keep a trailing line comment in the query from swallowing the closing parenthesis.
        string wrapped = $"SELECT * FROM (\n{inner}\n) t WHERE 1 = 0";
        var columns = await ReadSchemaAsync(connection, dialect, wrapped, [], CommandBehavior.Default, timeout, cancellationToken).ConfigureAwait(false);
        return new QueryShape(columns, DescribeStrategy.WrappedZeroRowProbe);
    }

    /// <summary>
    /// The prepared-statement strategy (PostgreSQL parse/describe): <see cref="CommandBehavior.SchemaOnly"/> makes the provider describe the
    /// statement without binding or executing it.
    /// </summary>
    public static async Task<QueryShape> SchemaOnlyAsync(DbConnection connection, ISqlDialect dialect, string sql, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string text = RequireReadOnly(sql, dialect);
        var columns = await ReadSchemaAsync(connection, dialect, text, [], CommandBehavior.SchemaOnly, timeout, cancellationToken).ConfigureAwait(false);
        return new QueryShape(columns, DescribeStrategy.PreparedStatement);
    }

    internal static async Task<IReadOnlyList<ColumnInfo>> ReadSchemaAsync(
        DbConnection connection, ISqlDialect dialect, string sql, object?[] args, CommandBehavior behavior, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Closed) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (timeout > TimeSpan.Zero) command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds);
        InformationSchemaCatalogReader.AddParameters(command, dialect, args);
        await using var reader = await command.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        return ReadColumns(reader, dialect);
    }

    /// <summary>The result columns of an open reader (names, types, nullability) from its schema table, falling back to name and CLR type.</summary>
    internal static IReadOnlyList<ColumnInfo> ReadColumns(DbDataReader reader, ISqlDialect dialect)
    {
        DataTable? schema = null;
        try { schema = reader.GetSchemaTable(); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or NotImplementedException) { /* fall back to the reader's own metadata */ }

        var columns = new List<ColumnInfo>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var row = schema is not null && i < schema.Rows.Count ? schema.Rows[i] : null;
            string name = reader.GetName(i);
            Type? clr = SafeFieldType(reader, i);
            string native = SafeTypeName(reader, i) ?? clr?.Name ?? "unknown";
            int? length = Num(row, "ColumnSize"), precision = Num(row, "NumericPrecision"), scale = Num(row, "NumericScale");
            if (precision is 255 or > 1000) precision = null;
            if (scale is 255 or < 0) scale = null;
            string text = TypeText.Compose(native, length, precision, scale);
            bool nullable = row is null || !schema!.Columns.Contains("AllowDBNull") || row["AllowDBNull"] is DBNull or true;
            bool identity = row is not null && schema!.Columns.Contains("IsAutoIncrement") && row["IsAutoIncrement"] is true;
            columns.Add(new ColumnInfo(name, i + 1, TypeText.ToCanonical(dialect, text, clr), text, nullable, IsIdentity: identity));
        }
        return columns;
    }

    private static int? Num(DataRow? row, string column) =>
        row is not null && row.Table.Columns.Contains(column) && row[column] is { } v and not DBNull ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static Type? SafeFieldType(DbDataReader reader, int i)
    {
        try { return reader.GetFieldType(i); } catch (Exception ex) when (ex is NotSupportedException or InvalidCastException) { return null; }
    }

    private static string? SafeTypeName(DbDataReader reader, int i)
    {
        try { return reader.GetDataTypeName(i) is { Length: > 0 } n ? n : null; } catch (Exception ex) when (ex is NotSupportedException or InvalidCastException) { return null; }
    }
}
