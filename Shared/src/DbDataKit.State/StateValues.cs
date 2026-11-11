using System.Globalization;

namespace DbDataKit.State;

/// <summary>
/// The convention types' values (plan 29 section 3.3), written and read the same way on every engine. SQL Server and PostgreSQL have real types for a UUID and a UTC instant and the
/// providers take <see cref="Guid"/> and <see cref="DateTimeOffset"/> directly. SQLite has neither, so they are text, in one fixed shape: a UUID in lower case, an instant as UTC
/// ISO 8601 with seven fractional digits and a <c>Z</c>, which sorts as time does (the retention job and every <c>*_at</c> comparison depend on that).
/// </summary>
public static class StateValues
{
    private const string SqliteTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>The value to hand a parameter on this engine: a <see cref="Guid"/> or <see cref="DateTimeOffset"/> as the engine stores it, anything else unchanged.</summary>
    public static object? ForParameter(StateDialect dialect, object? value) => value switch
    {
        Guid guid when dialect.Engine == StateEngineIds.Sqlite => guid.ToString("D", CultureInfo.InvariantCulture),
        DateTimeOffset time when dialect.Engine == StateEngineIds.Sqlite => FormatTime(time),
        decimal number when dialect.Engine == StateEngineIds.Sqlite => number.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset time => time.ToUniversalTime(),
        DateTime time => ForParameter(dialect, new DateTimeOffset(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime())),
        _ => value,
    };

    /// <summary>The text SQLite stores an instant as.</summary>
    public static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString(SqliteTimeFormat, CultureInfo.InvariantCulture);

    public static decimal ToDecimal(object value) => value switch
    {
        decimal number => number,
        string text => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    public static Guid ToGuid(object value) => value switch
    {
        Guid guid => guid,
        string text => Guid.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"A UUID column held a {value.GetType().Name}."),
    };

    public static DateTimeOffset ToTime(object value) => value switch
    {
        DateTimeOffset time => time.ToUniversalTime(),
        DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new InvalidCastException($"A time column held a {value.GetType().Name}."),
    };
}
