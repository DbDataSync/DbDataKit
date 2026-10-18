using System.Globalization;
using System.Text.RegularExpressions;

namespace DbDataBuild.Models;

/// <summary>Small, target-neutral facts about logical column types that the model loader needs (watermark literals, parameter type matching).</summary>
public static partial class ColumnTypes
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string Canon(string type)
    {
        var t = Spaces().Replace(type.Trim().ToUpperInvariant(), " ");
        t = t.Replace(" (", "(").Replace("( ", "(").Replace(" )", ")").Replace(", ", ",");
        return t switch { "INT" or "INT4" => "INTEGER", "INT8" => "BIGINT", "INT2" => "SMALLINT", "DATETIME" => "TIMESTAMP", _ => t };
    }

    /// <summary>Whether two spellings are the same logical type (synonyms match).</summary>
    public static bool Equivalent(string a, string b) => Canon(a) == Canon(b);

    /// <summary>Whether a literal is valid for a logical type: ISO dates, timestamps, integers.</summary>
    public static bool LiteralFits(string logicalType, string literal)
    {
        var t = Canon(logicalType);
        literal = literal.Trim();
        if (t == "DATE") return DateOnly.TryParseExact(literal, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal))
            return DateTime.TryParseExact(literal, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFF", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (t is "BIGINT" or "INTEGER" or "SMALLINT") return long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        return false;
    }
}
