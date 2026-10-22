using DbDataKit.Dialect;
namespace DbDataSync.Core.Sql;

/// <summary>
/// Plain SQL-standard answers to the mechanical variations in <see cref="SqlDialect"/>, for an engine
/// that is not known by name — the "Arrow Flight SQL" driver's default, where the server behind the
/// protocol could be anything. The other variants of that driver name a real engine and wrap its dialect
/// instead (see <c>DbDataSync.Drivers.ArrowFlightSql.FlightSqlDialect</c>).
/// <para>
/// Also the one place the *type names a Flight SQL column reports* are understood: that driver derives
/// a native type from each Arrow type in ANSI spelling (<c>bigint</c>, <c>double precision</c>,
/// <c>timestamp with time zone</c>, ...), whatever engine the proxy fronts, because an Arrow schema
/// carries no engine-specific type name to report instead.
/// </para>
/// </summary>
public sealed class AnsiDialect : SqlDialect
{
    public static AnsiDialect Instance { get; } = new();

    private AnsiDialect() { }

    protected override ISqlDialect? SharedDialect { get; } = DialectCatalog.Builtin.Get("ansi");

    public override string QuoteIdentifier(string identifier) => SharedDialect!.Quote(identifier);

    public override string ParameterReference(string name) => $"@{name}";

    /// <summary><c>FETCH FIRST n ROWS ONLY</c> rather than the base class's <c>LIMIT n</c>, which is not
    /// standard SQL.</summary>
    public override string RenderSampleSelect(string qualifiedTable, int rows) =>
        $"SELECT * FROM {qualifiedTable} FETCH FIRST {rows} ROWS ONLY";

    public override CanonicalType ToCanonicalType(string nativeType)
    {
        var (baseName, args) = CanonicalTypeSpec.Parse(nativeType);
        return baseName switch
        {
            "boolean" or "bool" => Simple(CanonicalTypeKind.Boolean),
            "tinyint" => Simple(CanonicalTypeKind.Int8),
            "smallint" => Simple(CanonicalTypeKind.Int16),
            "integer" or "int" => Simple(CanonicalTypeKind.Int32),
            "bigint" => Simple(CanonicalTypeKind.Int64),

            "decimal" or "numeric" => new CanonicalType(
                CanonicalTypeKind.Decimal, null,
                CanonicalTypeSpec.IntAt(args, 0, 18), CanonicalTypeSpec.IntAt(args, 1, 0), false, false),

            "real" or "float" => Simple(CanonicalTypeKind.Float),
            "double precision" or "double" => Simple(CanonicalTypeKind.Double),

            "varchar" or "character varying" or "char" or "character" or "text" => new CanonicalType(
                CanonicalTypeKind.String, CanonicalTypeSpec.IntAt(args, 0, null), null, null,
                IsUnicode: true, IsMax: args.Count == 0),

            "varbinary" or "binary" or "binary varying" or "blob" =>
                new CanonicalType(CanonicalTypeKind.Binary, null, null, null, false, true),

            "date" => Simple(CanonicalTypeKind.Date),
            "time" => Simple(CanonicalTypeKind.Time),
            "timestamp" => new CanonicalType(CanonicalTypeKind.Timestamp, null, null, 6, false, false),
            "timestamp with time zone" =>
                new CanonicalType(CanonicalTypeKind.TimestampTz, null, null, 6, false, false),

            _ => Simple(CanonicalTypeKind.Unmappable),
        };

        static CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false);
    }

    public override RenderedColumnType RenderColumnType(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => new("BOOLEAN", type.SourceNote),
        CanonicalTypeKind.Int8 => new("SMALLINT", Combine(type, "ANSI SQL has no one-byte integer; widened to SMALLINT.")),
        CanonicalTypeKind.Int16 => new("SMALLINT", type.SourceNote),
        CanonicalTypeKind.Int32 => new("INTEGER", type.SourceNote),
        CanonicalTypeKind.Int64 => new("BIGINT", type.SourceNote),
        CanonicalTypeKind.Decimal => new($"DECIMAL({type.Precision ?? 18},{type.Scale ?? 0})", type.SourceNote),
        CanonicalTypeKind.Float => new("REAL", type.SourceNote),
        CanonicalTypeKind.Double => new("DOUBLE PRECISION", type.SourceNote),
        CanonicalTypeKind.String when type.IsMax || type.Length is null => new("VARCHAR(4000)", Combine(type,
            "ANSI SQL has no unbounded VARCHAR; capped at 4000.")),
        CanonicalTypeKind.String => new($"VARCHAR({type.Length})", type.SourceNote),
        CanonicalTypeKind.Binary => new("VARBINARY(4000)", type.SourceNote),
        CanonicalTypeKind.Date => new("DATE", type.SourceNote),
        CanonicalTypeKind.Time => new("TIME", type.SourceNote),
        CanonicalTypeKind.Timestamp => new("TIMESTAMP", type.SourceNote),
        CanonicalTypeKind.TimestampTz => new("TIMESTAMP WITH TIME ZONE", type.SourceNote),
        CanonicalTypeKind.Guid => new("CHAR(36)", Combine(type, "ANSI SQL has no UUID type; stored as text.")),
        CanonicalTypeKind.Json or CanonicalTypeKind.Xml => new("VARCHAR(4000)", Combine(type,
            "ANSI SQL has no native document type; stored as text and not validated.")),
        CanonicalTypeKind.Unmappable => throw new InvalidOperationException(
            "RenderColumnType must never be called for Unmappable — the caller checks CanonicalType.Kind " +
            "first and reports the plan as Unsupported instead."),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, "Unknown canonical type kind."),
    };

    private static string Combine(CanonicalType type, string renderNote) =>
        type.SourceNote is null ? renderNote : $"{type.SourceNote} {renderNote}";
}
