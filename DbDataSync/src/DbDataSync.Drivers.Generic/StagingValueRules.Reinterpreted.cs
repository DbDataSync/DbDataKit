using DbDataKit.Dialect;

namespace DbDataSync.Drivers.Generic;

// Written by hand (A14b), unlike StagingValueRules.Data.cs: the pairs below are the "Proposed refusals" table of architecture/staging-value-types.md
// section 3a. Each is a (target kind, runtime type) the provider takes and stores as something that is not the source's value, whatever the value.
// StagingValueMatrixTests records every cell, so a change here moves cells in the expected files and the diff shows which. Pairs that are only a
// value-fit question (range, rounding, precision, text that parses) are deliberately not here.
public static partial class StagingValueRules
{
    /// <summary>
    /// SQL Server: a character into an integer column is stored as its code point (<c>'a'</c> as 97); a <see cref="DateTime"/> or <see cref="DateOnly"/> into
    /// <c>varchar</c>/<c>nvarchar</c> is formatted by the client (<c>03/17/2024 09:41:22</c>, lossy, culture-dependent). The second was kept lenient in A14b
    /// and refused by the owner's decision of 2026-10-30. The matrix measured no other date or time type stored as text (<see cref="DateTimeOffset"/>,
    /// <see cref="TimeOnly"/> and <see cref="TimeSpan"/> are already refused by the provider), and no <c>char</c>, <c>nchar</c>, <c>text</c> or <c>ntext</c> column.
    /// </summary>
    internal static readonly IReadOnlyDictionary<(CanonicalKind Kind, bool TimeZone), Type[]> SqlServerReinterpreted = new Dictionary<(CanonicalKind, bool), Type[]>
    {
        [(CanonicalKind.Int8, false)] = [typeof(char)],
        [(CanonicalKind.Int16, false)] = [typeof(char)],
        [(CanonicalKind.Int32, false)] = [typeof(char)],
        [(CanonicalKind.Int64, false)] = [typeof(char)],
        [(CanonicalKind.String, false)] = [typeof(DateTime), typeof(DateOnly)],
    };

    /// <summary>
    /// What to do about a reinterpretation, for the cases where it is not "change the type": a date into SQL Server text is formatted by the client, so the
    /// fix is a Transform that formats it the way the user wants. Null for every other pair. Never names the value.
    /// </summary>
    public static string? ReinterpretedAdvice(EngineId engine, CanonicalType target, Type valueType) =>
        engine == EngineId.SqlServer && target.Kind == CanonicalKind.String && (valueType == typeof(DateTime) || valueType == typeof(DateOnly))
            ? "A date or time sent into a text column is formatted by the client, lossy and depending on the culture of the machine that runs Sync. " +
              "Add a Transform to the mapped source column that formats the date or time as the text you want (for example 'yyyy-MM-dd HH:mm:ss')."
            : null;

    /// <summary>
    /// PostgreSQL: binary <c>COPY</c> writes an integer into <c>date</c> as days, and into <c>time</c>, <c>timestamp</c> and <c>timestamptz</c> as microseconds, since
    /// 2000-01-01; and bytes (a <c>byte[]</c> or a <see cref="Stream"/>) into <c>text</c> as UTF-8 characters.
    /// </summary>
    internal static readonly IReadOnlyDictionary<(CanonicalKind Kind, bool TimeZone), Type[]> PostgresReinterpreted = new Dictionary<(CanonicalKind, bool), Type[]>
    {
        [(CanonicalKind.Date, false)] = [typeof(int), typeof(ushort)],
        [(CanonicalKind.Time, false)] = [typeof(uint), typeof(long)],
        [(CanonicalKind.Timestamp, false)] = [typeof(uint), typeof(long)],
        [(CanonicalKind.Timestamp, true)] = [typeof(uint), typeof(long)],
        [(CanonicalKind.String, false)] = [typeof(byte[]), typeof(Stream)],
    };
}
