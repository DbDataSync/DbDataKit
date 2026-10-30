using DbDataKit.Dialect;

namespace DbDataSync.Drivers.Generic;

// Written by hand (A14b), unlike StagingValueRules.Data.cs: the pairs below are the "Proposed refusals" table of architecture/staging-value-types.md
// section 3a. Each is a (target kind, runtime type) the provider takes and stores as something that is not the source's value, whatever the value.
// StagingValueMatrixTests records every cell, so a change here moves cells in the expected files and the diff shows which. Pairs that are only a
// value-fit question (range, rounding, precision, text that parses) are deliberately not here.
public static partial class StagingValueRules
{
    /// <summary>SQL Server: a character into an integer column is stored as its code point (<c>'a'</c> as 97).</summary>
    internal static readonly IReadOnlyDictionary<(CanonicalKind Kind, bool TimeZone), Type[]> SqlServerReinterpreted = new Dictionary<(CanonicalKind, bool), Type[]>
    {
        [(CanonicalKind.Int8, false)] = [typeof(char)],
        [(CanonicalKind.Int16, false)] = [typeof(char)],
        [(CanonicalKind.Int32, false)] = [typeof(char)],
        [(CanonicalKind.Int64, false)] = [typeof(char)],
    };

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
