using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.Runner.Kit.Formats;

namespace DbDataKit.Runner.Kit.Changes;

/// <summary>
/// The hash of a row's values (plan 27a IX13): SHA-256 of a canonical text of the values, cut to 128 bits and written as base64 (22 characters), so a snapshot of a million rows stays a few tens of megabytes. The canonical text tells a missing value from an empty
/// text and a number from the same digits as text; numbers, dates and booleans are written in the invariant culture, a decimal without trailing zeros (1.50 and 1.5 are one value), a time as UTC. A whole number and a floating point number with the same digits
/// are different values (a source keeps one type per column, so that does not matter between two runs). The name of the algorithm is <see cref="Algorithm"/>: a snapshot made with another one is refused, never compared.
/// </summary>
public static class RowHash
{
    /// <summary>The name stored in a snapshot; changing how values are hashed means a new name.</summary>
    public const string Algorithm = "sha256-128/1";

    public static string Of(IEnumerable<object?> values)
    {
        var text = new StringBuilder();
        foreach (var value in values)
        {
            Append(text, value);
            text.Append('\u001f');
        }

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 16)).TrimEnd('=');
    }

    /// <summary>The hash of the named columns of a row (all its columns when <paramref name="columns"/> is null), in the order given. A column the row lacks is an <see cref="ArgumentException"/>.</summary>
    public static string Of(Row row, IReadOnlyList<string>? columns = null)
    {
        columns ??= row.Columns;
        return Of(columns.Select(c => row.Has(c) ? row[c] : throw new ArgumentException($"The row has no column '{c}'.", nameof(columns))));
    }

    private static void Append(StringBuilder text, object? value)
    {
        switch (value)
        {
            case null: text.Append('~'); break;
            case string s: text.Append('s').Append(s.Length).Append(':').Append(s); break;
            case bool b: text.Append('b').Append(b ? '1' : '0'); break;
            case decimal d: text.Append('n').Append(Normalize(d)); break;
            case double x: text.Append('f').Append(x.ToString("R", CultureInfo.InvariantCulture)); break;
            case float x: text.Append('f').Append(((double)x).ToString("R", CultureInfo.InvariantCulture)); break;
            case sbyte or byte or short or ushort or int or uint or long or ulong: text.Append('n').Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            case DateTime t: text.Append('t').Append((t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTimeOffset o: text.Append('t').Append(o.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateOnly d: text.Append('d').Append(d.ToString("O", CultureInfo.InvariantCulture)); break;
            case byte[] bytes: text.Append('x').Append(Convert.ToBase64String(bytes)); break;
            case Guid g: text.Append('g').Append(g.ToString("D")); break;
            case IFormattable f: text.Append('o').Append(f.ToString(null, CultureInfo.InvariantCulture)); break;
            default: text.Append('o').Append(value); break;
        }
    }

    private static string Normalize(decimal d)
    {
        var s = d.ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s == "-0" ? "0" : s;
    }
}
