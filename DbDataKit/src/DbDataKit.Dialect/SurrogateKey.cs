using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Dialect;

/// <summary>The type of one part of a business key. A part's type decides how its value is written as text before it is hashed.</summary>
public enum KeyPartType
{
    /// <summary>Text, with the spaces at both ends removed (and nothing else changed: no case folding, no Unicode normalization).</summary>
    Text,
    /// <summary>A whole number, written in decimal with no leading zeros and a `-` for a negative one.</summary>
    Integer,
    /// <summary>A date, written `yyyy-MM-dd`.</summary>
    Date,
    /// <summary>A moment without a time zone, written `yyyy-MM-dd HH:mm:ss` and cut (not rounded) to the second: the same text on every engine whatever its fractional precision. The valid-from of a history key.</summary>
    Timestamp,
}

/// <summary>One part of a business key: the SQL expression that produces its value, and its type.</summary>
public sealed record KeyPart(string Expression, KeyPartType Type);

/// <summary>
/// The surrogate key of plan 25 section 1, defined once: a 64-bit integer that is the first 8 bytes (big-endian, signed) of SHA-256 over the normalized business key. <see cref="Hash"/> is the reference; <see cref="Sql"/>
/// writes the same computation for an engine, and a test runs both over the same values, so a key is the same on every engine, in every channel and on every rebuild.
/// <para>
/// Normalization: each part is written as text by its type (<see cref="KeyPartType"/>), the parts are joined with U+001F (the unit separator) between them, and the text is encoded as UTF-8. A part that is NULL makes the
/// key NULL (the owning model decides what an unknown member is). Text is not case-folded: upper-casing differs between engines for anything but ASCII, so a key that must ignore case is made from a column the source
/// has already folded. A text part that itself holds U+001F can collide with another key; the generated uniqueness check (plan 23) is what finds that.
/// </para>
/// </summary>
public static class SurrogateKey
{
    public const char Separator = '\u001f';

    /// <summary>One part as text, or null when the value is NULL.</summary>
    public static string? PartText(object? value) => value switch
    {
        null => null,
        string s => s.Trim(' '),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"A key part is text, a whole number, a date or a moment, not {value.GetType().Name}."),
    };

    /// <summary>The normalized business key, or null when any part is NULL.</summary>
    public static string? Normalize(IReadOnlyList<object?> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("A key has at least one part.", nameof(parts));
        var texts = new string[parts.Count];
        for (var i = 0; i < parts.Count; i++) texts[i] = PartText(parts[i]) ?? "";
        return parts.Any(p => p is null) ? null : string.Join(Separator, texts);
    }

    /// <summary>The key of a normalized business key.</summary>
    public static long Hash(string normalized) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    /// <summary>The key of a business key, or null when any part is NULL.</summary>
    public static long? Compute(params object?[] parts) => Normalize(parts) is { } n ? Hash(n) : null;

    /// <summary>
    /// The name of the marker function a lowered query carries for a key call: `ddb_key_` and one letter per part (`t` text, `i` whole number, `d` date, `m` moment), `ddb_key_ti`. The lowerer keeps the call, with its arguments, so the
    /// text of each engine is written when the query is rendered for it (<see cref="FromMarker"/>) rather than found again in what DuckDB expanded.
    /// </summary>
    public static string MarkerName(IReadOnlyList<KeyPartType> types) => "ddb_key_" + string.Concat(types.Select(t => t switch { KeyPartType.Text => 't', KeyPartType.Integer => 'i', KeyPartType.Date => 'd', _ => 'm' }));

    /// <summary>The part types a marker name stands for, or null when the name is not a marker.</summary>
    public static IReadOnlyList<KeyPartType>? TypesOfMarker(string name)
    {
        if (!name.StartsWith("ddb_key_", StringComparison.OrdinalIgnoreCase) || name.Length == "ddb_key_".Length) return null;
        var types = new List<KeyPartType>();
        foreach (var c in name["ddb_key_".Length..].ToLowerInvariant())
        {
            switch (c)
            {
                case 't': types.Add(KeyPartType.Text); break;
                case 'i': types.Add(KeyPartType.Integer); break;
                case 'd': types.Add(KeyPartType.Date); break;
                case 'm': types.Add(KeyPartType.Timestamp); break;
                default: return null;
            }
        }
        return types;
    }

    /// <summary>The key of a marker call: the SQL for the engine over the marker's arguments (already written in the engine's dialect), or null when the marker's name or argument count is not one this writes.</summary>
    public static string? FromMarker(string engine, string markerName, IReadOnlyList<string> arguments)
    {
        var types = TypesOfMarker(markerName);
        if (types == null || types.Count != arguments.Count) return null;
        return Sql(engine, arguments.Select((a, i) => new KeyPart(a, types[i])).ToList());
    }

    /// <summary>The engines <see cref="Sql"/> writes for.</summary>
    public static readonly IReadOnlyList<string> Engines = ["duckdb", "sqlserver", "fabric", "postgres"];

    /// <summary>
    /// The expression that computes the key on an engine from the parts' expressions. SQL Server and Fabric hash the UTF-8 bytes (a UTF-8 collation, available from SQL Server 2019); PostgreSQL's `sha256` and DuckDB's
    /// are over UTF-8 already. DuckDB returns hex text, so it takes the first 8 bytes as two 32-bit halves, which also gives the signed value without an overflow.
    /// </summary>
    public static string Sql(string engine, IReadOnlyList<KeyPart> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("A key has at least one part.", nameof(parts));
        var e = engine.ToLowerInvariant();
        if (!Engines.Contains(e)) throw new NotSupportedException($"No surrogate key is defined for engine '{engine}'; it is defined for {string.Join(", ", Engines)}.");
        var texts = parts.Select(p => PartSql(e, p)).ToList();
        var isNull = string.Join(" OR ", parts.Select(p => $"{p.Expression} IS NULL"));
        var joined = e is "duckdb" or "postgres"
            ? string.Join(e == "duckdb" ? " || chr(31) || " : " || chr(31) || ", texts)
            : string.Join(" + NCHAR(31) + ", texts);
        return $"CASE WHEN {isNull} THEN NULL ELSE {Wrap(e, joined)} END";
    }

    private static string PartSql(string e, KeyPart p) => (e, p.Type) switch
    {
        ("sqlserver" or "fabric", KeyPartType.Text) => $"LTRIM(RTRIM(CAST({p.Expression} AS NVARCHAR(4000))))",
        ("sqlserver" or "fabric", KeyPartType.Integer) => $"CAST(CAST({p.Expression} AS BIGINT) AS NVARCHAR(20))",
        ("sqlserver" or "fabric", KeyPartType.Date) => $"CONVERT(NVARCHAR(10), CAST({p.Expression} AS DATE), 23)",
        // style 120 is `yyyy-mm-dd hh:mi:ss`; written from a DATETIME2(7) it cuts the fraction instead of rounding it
        ("sqlserver" or "fabric", KeyPartType.Timestamp) => $"CONVERT(NVARCHAR(19), CAST({p.Expression} AS DATETIME2(7)), 120)",
        ("postgres", KeyPartType.Text) => $"btrim(CAST({p.Expression} AS text), ' ')",
        ("postgres", KeyPartType.Integer) => $"CAST(CAST({p.Expression} AS bigint) AS text)",
        ("postgres", KeyPartType.Date) => $"to_char(CAST({p.Expression} AS date), 'YYYY-MM-DD')",
        ("postgres", KeyPartType.Timestamp) => $"to_char(CAST({p.Expression} AS timestamp), 'YYYY-MM-DD HH24:MI:SS')",
        ("duckdb", KeyPartType.Text) => $"trim(CAST({p.Expression} AS VARCHAR), ' ')",
        ("duckdb", KeyPartType.Integer) => $"CAST(CAST({p.Expression} AS BIGINT) AS VARCHAR)",
        ("duckdb", KeyPartType.Date) => $"strftime(CAST({p.Expression} AS DATE), '%Y-%m-%d')",
        ("duckdb", KeyPartType.Timestamp) => $"strftime(CAST({p.Expression} AS TIMESTAMP), '%Y-%m-%d %H:%M:%S')",
        _ => throw new NotSupportedException(),
    };

    private static string Wrap(string e, string joined) => e switch
    {
        "sqlserver" or "fabric" =>
            // the collation is put on the text before it is cast to VARCHAR, which is what makes the cast write UTF-8 (put after the cast it would only relabel characters the cast had already lost)
            $"CAST(SUBSTRING(HASHBYTES('SHA2_256', CAST(({joined}) COLLATE Latin1_General_100_CI_AS_SC_UTF8 AS VARCHAR(MAX))), 1, 8) AS BIGINT)",
        "postgres" =>
            $"CAST(CAST('x' || encode(substring(sha256(convert_to({joined}, 'UTF8')) from 1 for 8), 'hex') AS bit(64)) AS bigint)",
        _ => DuckDbHash(joined),
    };

    private static string DuckDbHash(string joined)
    {
        // hi and lo are the first and second 32 bits as non-negative BIGINTs; hi is made signed by taking 2^32 off when its top bit is set
        var h = $"sha256({joined})";
        var hiValue = $"(CAST(('0x' || substr({h}, 1, 8)) AS BIGINT))";
        var loValue = $"(CAST(('0x' || substr({h}, 9, 8)) AS BIGINT))";
        return $"((CASE WHEN {hiValue} >= 2147483648 THEN {hiValue} - 4294967296 ELSE {hiValue} END) * 4294967296 + {loValue})";
    }
}
