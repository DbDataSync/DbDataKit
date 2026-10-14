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

    private static string? Text(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull) return null;
        string? value = row[column]?.ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
