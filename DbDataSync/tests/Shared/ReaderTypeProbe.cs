using System.Data.Common;

namespace DbDataSync.TestSupport;

/// <summary>
/// What a source driver's reader actually returns from <c>GetValue</c> for each column type: the half of the staging type table that stands before the
/// target. One engine's test creates a table per case, inserts a literal, reads it back through the same provider the driver reads with and writes
/// <c>native type TAB field type TAB value type TAB value</c>; the lines are compared with the checked-in <c>ReaderTypes.*.expected.tsv</c>.
/// </summary>
public static class ReaderTypeProbe
{
    public sealed record Case(string NativeType, string Literal);

    /// <summary>Reads the first cell of the command's single row. A reader that throws is a finding too (a value the driver cannot hand over at all).</summary>
    public static async Task<string> ReadAsync(DbCommand select, string nativeType)
    {
        try
        {
            await using var reader = await select.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return $"{nativeType}\t-\t-\tNO ROW";
            var declared = SafeFieldType(reader);
            object value;
            try { value = reader.GetValue(0); }
            catch (Exception ex) { return $"{nativeType}\t{declared}\t-\tGetValue THROWS {ex.GetType().Name}"; }
            return $"{nativeType}\t{declared}\t{(value is DBNull ? "DBNull" : value.GetType().Name)}\t{StagingMatrix.Describe(value)}";
        }
        catch (Exception ex)
        {
            return $"{nativeType}\t-\t-\tQUERY THROWS {ex.GetType().Name}";
        }
    }

    private static string SafeFieldType(DbDataReader reader)
    {
        try { return reader.GetFieldType(0)?.Name ?? "null"; }
        catch (Exception ex) { return "THROWS " + ex.GetType().Name; }
    }
}
