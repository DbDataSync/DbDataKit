using System.Security.Cryptography;
using System.Text;

namespace DbDataBuild.State;

/// <summary>One column as a catalog (or a model definition) reports it. Only what the shape hash covers (DESIGN.md 12.1).</summary>
/// <param name="Type">The engine's type name, lower case, without length or precision (`varchar`, `decimal`, `bigint`).</param>
/// <param name="Length">Character length, or null. -1 means MAX/unbounded.</param>
/// <param name="Computed">The computed-column definition text, or null.</param>
public sealed record ColumnShape(
    string Name, string Type, int? Length, int? Precision, int? Scale, bool Nullable, string? Collation, string? Computed = null);

/// <summary>An index, partitioning or compression setting: the physical hash covers these and nothing about columns.</summary>
public sealed record PhysicalItem(string Kind, string Name, string Definition);

/// <summary>
/// The hashes of DESIGN.md 12.1. Each hashes a canonical text that is built here and nowhere else, so a hash only changes when what it
/// covers changes. Column order is deliberately not part of the shape hash (the tool always emits explicit column lists); it is recorded separately.
/// </summary>
public static class Hashing
{
    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Column names, types, lengths, precision, scale, nullability, collation and computed definitions, sorted by name (ordinal, case-sensitive).</summary>
    public static string ShapeHash(IEnumerable<ColumnShape> columns) => Sha256Hex(ShapeText(columns));

    public static string ShapeText(IEnumerable<ColumnShape> columns)
    {
        var sb = new StringBuilder("shape/v1\n");
        foreach (var c in columns.OrderBy(c => c.Name, StringComparer.Ordinal))
            sb.Append(Field(c.Name)).Append('|').Append(Field(c.Type.ToLowerInvariant())).Append('|').Append(Num(c.Length)).Append('|').Append(Num(c.Precision)).Append('|')
              .Append(Num(c.Scale)).Append('|').Append(c.Nullable ? "null" : "notnull").Append('|').Append(Field(c.Collation)).Append('|').Append(Field(c.Computed)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Indexes, partitioning and compression, sorted so that catalog enumeration order cannot change the hash.</summary>
    public static string PhysicalHash(IEnumerable<PhysicalItem> items)
    {
        var sb = new StringBuilder("physical/v1\n");
        foreach (var i in items.OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal))
            sb.Append(Field(i.Kind)).Append('|').Append(Field(i.Name)).Append('|').Append(Field(i.Definition)).Append('\n');
        return Sha256Hex(sb.ToString());
    }

    /// <summary>Column names in ordinal order, stored beside the shape hash (not inside it).</summary>
    public static string OrdinalText(IEnumerable<string> columnNamesInOrder) => string.Join(",", columnNamesInOrder);

    /// <summary>Hash of a rendered script for one target (the per-target rendered-SQL hashes of DESIGN.md 12.1). Line endings are normalized.</summary>
    public static string ScriptHash(string text) => Sha256Hex(text.Replace("\r\n", "\n"));

    // `|` and newlines in names or definitions must not let two different inputs share a canonical text
    private static string Field(string? v) => v == null ? "~" : v.Replace("\\", "\\\\").Replace("|", "\\p").Replace("\n", "\\n").Replace("~", "\\t");
    private static string Num(int? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "~";
}
