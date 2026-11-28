using DbDataKit.Dialect;

namespace DbDataSync.Core.Sql;

/// <summary>
/// How a Flight SQL driver reads a column's type. An Arrow schema carries no engine-specific type name, so a Flight SQL column reports an ANSI
/// spelling (<c>bigint</c>, <c>double precision</c>, <c>timestamp with time zone</c>, ...) whatever engine the server fronts; a name ANSI does not
/// have is read as the fronted engine's own. The Flight SQL driver's dialect and the offline check of a sync project both read types this way.
/// </summary>
public static class FlightSqlTypes
{
    /// <summary>The canonical type of <paramref name="nativeType"/>: the ANSI reading when ANSI maps it, otherwise <paramref name="inner"/>'s.</summary>
    /// <param name="inner">The dialect of the engine behind the server (<see cref="AnsiDialect.Instance"/> when the connection names none).</param>
    public static TypeMapping<CanonicalType> Read(SqlDialect inner, string nativeType) =>
        AnsiDialect.Instance.ReadColumnType(nativeType) is { IsMapped: true } ansi ? ansi : inner.ReadColumnType(nativeType);
}
