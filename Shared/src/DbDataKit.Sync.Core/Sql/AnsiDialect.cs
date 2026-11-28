using DbDataKit.Dialect;
namespace DbDataKit.Sync.Core.Sql;

/// <summary>
/// Plain SQL-standard answers to the mechanical variations in <see cref="SqlDialect"/>, for an engine
/// that is not known by name — the "Arrow Flight SQL" driver's default, where the server behind the
/// protocol could be anything. The other variants of that driver name a real engine and wrap its dialect
/// instead (see <c>DbDataSync.Drivers.ArrowFlightSql.FlightSqlDialect</c>).
/// <para>
/// Its types are the shared <c>flightsql</c> dialect's: that driver derives a native type from each Arrow type in
/// ANSI spelling (<c>bigint</c>, <c>double precision</c>, <c>timestamp with time zone</c>, ...), whatever engine the proxy
/// fronts, because an Arrow schema carries no engine-specific type name to report instead.
/// </para>
/// </summary>
public sealed class AnsiDialect : SqlDialect
{
    public static AnsiDialect Instance { get; } = new();

    private AnsiDialect() { }

    private static readonly ISqlDialect Shared = DialectCatalog.Builtin.Get("ansi");

    public override string QuoteIdentifier(string identifier) => SharedQuoting.Quote(Shared, identifier);

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary><c>FETCH FIRST n ROWS ONLY</c> rather than the base class's <c>LIMIT n</c>, which is not
    /// standard SQL.</summary>
    public override string RenderSampleSelect(string qualifiedTable, int rows) =>
        $"SELECT * FROM {qualifiedTable} FETCH FIRST {rows} ROWS ONLY";

    /// <summary>The shared <c>flightsql</c> table: the ANSI names a Flight SQL client gives Arrow columns, and the types written for a target
    /// whose engine is not named.</summary>
    private static readonly ISqlDialect Types = DialectCatalog.Builtin.Get("flightsql");

    public override TypeMapping<CanonicalType> ReadColumnType(string nativeType) => DialectTypes.Read(Types, nativeType);

    public override RenderedColumnType WriteColumnType(CanonicalType type, string? sourceNote = null) =>
        DialectTypes.Write(Types, type, sourceNote);
}
