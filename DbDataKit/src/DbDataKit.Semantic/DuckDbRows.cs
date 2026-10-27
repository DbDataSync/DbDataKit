using System.Data.Common;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>Reads a DuckDB command's rows the way the engine wants them: whole numbers as <c>long</c>, dates as ISO 8601 text.</summary>
public static class DuckDbRows
{
    /// <summary>
    /// DuckDB returns HUGEINT (for example SUM over BIGINT) as BigInteger, which doesn't serialize as a JSON number.
    /// A date column becomes an ISO 8601 date string, whichever .NET type the driver used for it.
    /// </summary>
    private static object Normalize(object v, string typeName) => v switch
    {
        System.Numerics.BigInteger b => b >= long.MinValue && b <= long.MaxValue ? (long)b : (decimal)b,
        DateOnly d => d.ToString("yyyy-MM-dd"),
        DateTime t when typeName.Equals("date", StringComparison.OrdinalIgnoreCase) => t.ToString("yyyy-MM-dd"),
        _ => v,
    };

    /// <summary>Runs the command and reads up to <paramref name="maxRows"/> rows.</summary>
    public static QueryRows Read(DbCommand cmd, int maxRows)
    {
        using var reader = cmd.ExecuteReader();
        var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (rows.Count < maxRows && reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < cols.Count; i++)
                row[cols[i]] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i), reader.GetDataTypeName(i));
            rows.Add(row);
        }
        return new QueryRows(cols, rows);
    }
}
