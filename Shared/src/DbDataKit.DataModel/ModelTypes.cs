using System.Globalization;
using DbDataKit.Dialect;

namespace DbDataKit.DataModel;

/// <summary>Reads the type of an attribute: a SQL name as people write it, or the canonical text of <see cref="CanonicalType"/>.</summary>
public static class ModelTypes
{
    public static bool TryParse(string text, out CanonicalType? type, out string? error)
    {
        type = null; error = null;
        text = text.Trim();
        var open = text.IndexOf('(');
        var name = (open < 0 ? text : text[..open]).Trim().ToUpperInvariant();
        var args = open < 0 || !text.EndsWith(')') ? [] : text[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (open >= 0 && !text.EndsWith(')')) { error = $"`{text}` is missing a closing parenthesis."; return false; }

        int? Number(int index) => index < args.Length && int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (args.Any(a => !int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out _)) && !CanonicalType.TryParse(text, out _, out _))
        { error = $"The arguments of `{text}` must be whole numbers."; return false; }

        CanonicalType? found = name switch
        {
            "BOOLEAN" or "BOOL" => CanonicalType.Of(CanonicalKind.Boolean),
            "TINYINT" => CanonicalType.Of(CanonicalKind.Int8),
            "SMALLINT" => CanonicalType.Of(CanonicalKind.Int16),
            "INTEGER" or "INT" => CanonicalType.Of(CanonicalKind.Int32),
            "BIGINT" => CanonicalType.Of(CanonicalKind.Int64),
            "DECIMAL" or "NUMERIC" => args.Length == 2 && Number(0) is { } p && Number(1) is { } s ? CanonicalType.Decimal(p, s) : null,
            "REAL" or "FLOAT" => CanonicalType.Of(CanonicalKind.Float),
            "DOUBLE" => CanonicalType.Of(CanonicalKind.Double),
            "VARCHAR" or "CHAR" or "TEXT" or "STRING" => args.Length <= 1 ? CanonicalType.String(Number(0)) : null,
            "BLOB" or "BINARY" or "VARBINARY" => args.Length <= 1 ? CanonicalType.Binary(Number(0)) : null,
            "DATE" => CanonicalType.Of(CanonicalKind.Date),
            "TIME" => CanonicalType.Time(Number(0)),
            "TIMESTAMP" => CanonicalType.Timestamp(Number(0)),
            "TIMESTAMPTZ" => CanonicalType.Timestamp(Number(0), tz: true),
            "UUID" => CanonicalType.Of(CanonicalKind.Guid),
            "JSON" => CanonicalType.Of(CanonicalKind.Json),
            _ => null,
        };
        if (found is not null) { type = found; return true; }

        if (name is not ("DECIMAL" or "NUMERIC") && CanonicalType.TryParse(text, out var canonical, out _) && canonical!.Kind != CanonicalKind.Unmappable) { type = canonical; return true; }
        error = name is "DECIMAL" or "NUMERIC" ? $"`{text}` needs a precision and a scale, for example `DECIMAL(10,2)`."
            : $"`{text}` is not a type the models know. Use a name like VARCHAR(40), INTEGER, DECIMAL(10,2), BOOLEAN, DATE or TIMESTAMP.";
        return false;
    }

    /// <summary>Whether text can be the value of an attribute of this type (for a <c>default</c> or an <c>allowed</c> entry).</summary>
    public static bool Holds(CanonicalType type, string value) => type.Kind switch
    {
        CanonicalKind.Boolean => bool.TryParse(value, out _),
        CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64 => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        CanonicalKind.Decimal or CanonicalKind.Float or CanonicalKind.Double => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        CanonicalKind.Date or CanonicalKind.Timestamp => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        CanonicalKind.Guid => Guid.TryParse(value, out _),
        CanonicalKind.String => type.Length is not { } length || value.Length <= length,
        _ => true,
    };
}
