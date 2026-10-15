using System.Globalization;

namespace DbData.Metadata;

/// <summary>One row of a catalog query: values by position or by (case-insensitive) column name, with NULL as null.</summary>
public sealed class CatalogRow(string[] names, object?[] values)
{
    public int Count => values.Length;
    public object? this[int index] => index < values.Length && values[index] is not DBNull ? values[index] : null;
    public object? this[string name] => Array.FindIndex(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? this[i] : null;

    public string? Text(int index) => this[index] is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    public string? Text(string name) => this[name] is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    public int? Int(int index) => ToInt(this[index]);
    public int? Int(string name) => ToInt(this[name]);
    public long? Long(int index) => this[index] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
    public bool Bool(int index, bool whenNull = false) => ToBool(this[index]) ?? whenNull;
    public bool Bool(string name, bool whenNull = false) => ToBool(this[name]) ?? whenNull;

    private static int? ToInt(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static bool? ToBool(object? v) => v switch
    {
        null => null,
        bool b => b,
        string s => s.Trim().ToUpperInvariant() switch { "YES" or "Y" or "TRUE" or "T" or "1" => true, "NO" or "N" or "FALSE" or "F" or "0" => false, _ => null },
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0,
    };
}
