using System.Data.Common;

namespace DbDataKit.Semantic.Abstractions;

/// <summary>Reads a command's rows the way the engine wants them, whichever engine ran it: whole numbers as <c>long</c>, dates as ISO 8601 text.</summary>
public static class QueryRowReader
{
    /// <summary>
    /// DuckDB returns HUGEINT (for example SUM over BIGINT) as BigInteger, which doesn't serialize as a JSON number.
    /// A date column becomes an ISO 8601 date string, whichever .NET type the driver used for it.
    /// </summary>
    private static object Normalize(object v, bool dateColumn) => v switch
    {
        System.Numerics.BigInteger b => b >= long.MinValue && b <= long.MaxValue ? (long)b : (decimal)b,
        DateOnly d => d.ToString("yyyy-MM-dd"),
        DateTime t when dateColumn => t.ToString("yyyy-MM-dd"),
        _ => v,
    };

    /// <summary>Runs the command and reads up to <paramref name="maxRows"/> rows.</summary>
    public static QueryRows Read(DbCommand cmd, int maxRows)
    {
        using var reader = cmd.ExecuteReader();
        return ReadRows(reader, maxRows);
    }

    /// <summary>Runs the command and reads it into Arrow arrays with typed getters; a column type without an Arrow form here falls back to the row read on the same reader.</summary>
    public static ColumnarOutcome ReadColumnar(DbCommand cmd, int maxRows)
    {
        using var reader = cmd.ExecuteReader();
        return ArrowRows.TryRead(reader, maxRows) is { } columnar ? new(columnar, null) : new(null, ReadRows(reader, maxRows));
    }

    internal static QueryRows ReadRows(DbDataReader reader, int maxRows)
    {
        var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        // the type name is a property of the column, not of each cell: asking for it per value cost more than the rest of the read
        var dateColumn = Enumerable.Range(0, cols.Count).Select(i => reader.GetDataTypeName(i).Equals("date", StringComparison.OrdinalIgnoreCase)).ToArray();
        var rows = new List<Dictionary<string, object?>>();
        while (rows.Count < maxRows && reader.Read())
        {
            var row = new Dictionary<string, object?>(cols.Count);
            for (int i = 0; i < cols.Count; i++)
                row[cols[i]] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i), dateColumn[i]);
            rows.Add(row);
        }
        return new QueryRows(cols, rows);
    }
}
