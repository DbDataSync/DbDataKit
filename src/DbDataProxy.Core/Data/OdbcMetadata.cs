using System.Data;
using System.Data.Odbc;

namespace DbDataProxy.Data;

/// <summary>Table listing through the ODBC catalog functions (SQLTables), exposed by ADO.NET as GetSchema.</summary>
public static class OdbcMetadata
{
    public static async Task<IReadOnlyList<TableEntry>> ListTablesAsync(OdbcConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<TableEntry>();
        // The "Tables" collection asks the driver for TABLE objects and "Views" for VIEW objects.
        foreach (string collection in new[] { "Tables", "Views" })
        {
            DataTable table;
            try { table = connection.GetSchema(collection); }
            catch (Exception ex) when (ex is OdbcException or NotSupportedException or InvalidOperationException)
            {
                // Some drivers do not implement one of the two; the other may still work.
                continue;
            }
            string type = collection == "Views" ? "VIEW" : "TABLE";
            foreach (DataRow row in table.Rows)
            {
                string? name = Text(row, "TABLE_NAME");
                if (name is null) continue;
                entries.Add(new TableEntry(Text(row, "TABLE_CAT"), Text(row, "TABLE_SCHEM"), name, Text(row, "TABLE_TYPE") is { } t && t.Length > 0 ? t : type));
            }
        }
        return entries;
    }


    /// <summary>
    /// Asks the driver for its identifier quote character. OdbcCommandBuilder.QuoteIdentifier calls SQLGetInfo
    /// (SQL_IDENTIFIER_QUOTE_CHAR) and returns the name unchanged when the driver reports no quoting (" ").
    /// </summary>
    public static async Task<IdentifierQuote?> GetIdentifierQuoteAsync(OdbcConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string probe = "x";
        string quoted = new OdbcCommandBuilder().QuoteIdentifier(probe, connection);
        int at = quoted.IndexOf(probe, StringComparison.Ordinal);
        return quoted == probe || at < 0 ? null : new IdentifierQuote(quoted[..at], quoted[(at + probe.Length)..]);
    }

    /// <summary>Declared columns of one table (SQLColumns). The catalog functions treat names as patterns, so rows are matched exactly here.</summary>
    public static async Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(
        OdbcConnection connection, string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<ColumnEntry>();
        DataTable result;
        try { result = connection.GetSchema("Columns", [catalog, schema, table, null]); }
        catch (Exception ex) when (ex is OdbcException or NotSupportedException or InvalidOperationException) { return columns; }

        foreach (DataRow row in result.Rows)
        {
            if (!string.Equals(Text(row, "TABLE_NAME"), table, StringComparison.Ordinal)) continue;
            if (Text(row, "COLUMN_NAME") is not { } name) continue;
            bool nullable = !string.Equals(Text(row, "IS_NULLABLE"), "NO", StringComparison.OrdinalIgnoreCase);
            columns.Add(new ColumnEntry(name, nullable, Number(row, "COLUMN_SIZE"), Number(row, "DECIMAL_DIGITS")));
        }
        return columns;
    }

    private static int? Number(DataRow row, string column) =>
        Text(row, column) is { } text && int.TryParse(text, out int value) ? value : null;

    private static string? Text(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull) return null;
        string? value = row[column]?.ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
