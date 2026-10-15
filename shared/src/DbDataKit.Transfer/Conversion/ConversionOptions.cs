using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>
/// What happens to a value that does not fit its target column. Errors always name the column, never the value.
/// <list type="bullet">
/// <item><c>Fail</c> (default): throw <see cref="ValueConversionException"/>.</item>
/// <item><c>Null</c>: write NULL (still fails when the target column is NOT NULL).</item>
/// <item><c>Truncate</c>: reduce precision, length or text (cut strings and binary, drop fractional digits and sub-scale time, strip NUL, replace unpaired surrogates with U+FFFD). Range violations still fail.</item>
/// <item><c>Clamp</c>: everything <c>Truncate</c> does, plus saturate to the nearest representable value (integer, decimal and time-of-day ranges, infinity to the largest finite value). NaN cannot be clamped and fails.</item>
/// </list>
/// </summary>
public enum ErrorPolicy { Fail, Null, Truncate, Clamp }

/// <summary>Per-column options. <see cref="ForDialect"/> sets the engine facts that decide which values are unrepresentable.</summary>
public sealed record ConversionOptions
{
    public ErrorPolicy Policy { get; init; } = ErrorPolicy.Fail;
    /// <summary>NaN and the infinities are legal values of the target (PostgreSQL, DuckDB). SQL Server, MySQL and Oracle NUMBER refuse them.</summary>
    public bool AllowNonFinite { get; init; }
    /// <summary>The engine cannot store U+0000 in text (PostgreSQL).</summary>
    public bool RejectNul { get; init; }
    /// <summary>The engine stores UTF-8 or validates UTF-16, so an unpaired surrogate cannot be stored.</summary>
    public bool RejectUnpairedSurrogates { get; init; } = true;

    public static ConversionOptions ForDialect(ISqlDialect? dialect, ErrorPolicy policy = ErrorPolicy.Fail)
    {
        var id = dialect?.Id.Value ?? "ansi";
        return new ConversionOptions
        {
            Policy = policy,
            AllowNonFinite = id is "postgres" or "duckdb",
            RejectNul = id is "postgres",
            RejectUnpairedSurrogates = id is not "sqlserver" and not "fabric",
        };
    }
}
