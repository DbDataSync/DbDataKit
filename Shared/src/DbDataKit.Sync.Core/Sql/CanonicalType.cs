using DbDataKit.Dialect;
using Shared = DbDataKit.Dialect;

namespace DbDataSync.Core.Sql;

/// <summary>
/// The kinds of <see cref="CanonicalType"/>, the shape authors' scripts and <c>driver.csx</c> files see a column type in
/// (<c>IScriptDialect</c>, <c>ProvisioningColumn</c>, a dialect's <see cref="SqlDialect.ToCanonicalType"/>). The type rules themselves are the shared
/// dialects' (<see cref="Shared.CanonicalType"/>); <see cref="CanonicalType.FromShared(TypeMapping{Shared.CanonicalType})"/> and
/// <see cref="CanonicalType.ToShared"/> convert between the two without changing an answer.
/// </summary>
public enum CanonicalTypeKind
{
    Boolean,
    Int8,
    Int16,
    Int32,
    Int64,
    Decimal,
    Float,
    Double,
    String,
    Binary,
    Date,
    Time,
    Timestamp,
    TimestampTz,
    Guid,
    Json,
    Xml,

    /// <summary>No canonical translation exists — <c>sql_variant</c>, <c>hierarchyid</c>,
    /// <c>geography</c>, an array type, an enum. A provisioning plan that hits this reports
    /// <c>Unsupported</c> naming the column and its native type; nothing ever guesses a rendering
    /// for it.</summary>
    Unmappable,
}

/// <param name="Length">Character/byte length for <see cref="CanonicalTypeKind.String"/> and
/// <see cref="CanonicalTypeKind.Binary"/>; null when <paramref name="IsMax"/> is true or the kind
/// carries no length.</param>
/// <param name="Precision">Total digits, for <see cref="CanonicalTypeKind.Decimal"/>.</param>
/// <param name="Scale">Digits after the point for <see cref="CanonicalTypeKind.Decimal"/>, or
/// sub-second fractional digits for <see cref="CanonicalTypeKind.Time"/>/<see cref="CanonicalTypeKind.Timestamp"/>/
/// <see cref="CanonicalTypeKind.TimestampTz"/>.</param>
/// <param name="IsUnicode">Only meaningful for <see cref="CanonicalTypeKind.String"/>: SQL Server
/// distinguishes <c>varchar</c> from <c>nvarchar</c>; an engine with one string type (Postgres) always
/// reports true so a round trip through it never narrows.</param>
/// <param name="IsMax">The type has no length bound (<c>nvarchar(max)</c>, <c>varbinary(max)</c>,
/// Postgres <c>text</c>/<c>bytea</c>).</param>
/// <param name="SourceNote">A caveat already known at the moment the *native* type was collapsed into
/// this canonical shape — set by <c>ToCanonicalType</c>, not by the renderer. SQL Server's
/// <c>money</c> is the motivating case: it maps to <see cref="CanonicalTypeKind.Decimal"/> like any
/// other fixed-point type, but the collapse itself is where its currency semantics are lost, before
/// any target has been chosen. <see cref="RenderedColumnType.Fidelity"/> folds this in with whatever
/// the render side finds, so a caller only ever has one warning to read rather than two places to
/// check.</param>
/// <param name="IsFixed">Only meaningful for <see cref="CanonicalTypeKind.Binary"/>: the column is a fixed-length <c>binary(n)</c> (SQL Server's
/// <c>rowversion</c> included, always 8 bytes) rather than a variable-length <c>varbinary(n)</c>; the shared <c>binary(fixed,n)</c>.</param>
/// <param name="IsUnsigned">Only meaningful for the integer kinds: the column holds no negative values and the full unsigned range (SQL Server's
/// <c>tinyint</c>, MySQL's <c>int unsigned</c>, DuckDB's <c>UBIGINT</c>). An engine without unsigned types writes the next wider signed type.</param>
public sealed record CanonicalType(
    CanonicalTypeKind Kind,
    int? Length,
    int? Precision,
    int? Scale,
    bool IsUnicode,
    bool IsMax,
    string? SourceNote = null,
    bool IsFixed = false,
    bool IsUnsigned = false)
{
    /// <summary>
    /// A shared dialect's reading as this shape. The reading's note is kept when it is not exact. Kinds Sync does not replicate
    /// (an interval, a list) are <see cref="CanonicalTypeKind.Unmappable"/> with a note saying so, as is a type the dialect does not know.
    /// </summary>
    public static CanonicalType FromShared(TypeMapping<Shared.CanonicalType> mapping)
    {
        if (!mapping.IsMapped || mapping.Result is not { } t)
            return new CanonicalType(CanonicalTypeKind.Unmappable, null, null, null, false, false, mapping.Note);
        var note = mapping.Fidelity == Fidelity.Exact ? null : mapping.Note;
        return FromShared(t, note);
    }

    /// <summary>The shared type as this shape, with <paramref name="note"/> as its <see cref="SourceNote"/>.</summary>
    public static CanonicalType FromShared(Shared.CanonicalType t, string? note = null)
    {
        var unsigned = t.Unsigned == true;
        return t.Kind switch
        {
            CanonicalKind.Boolean => Simple(CanonicalTypeKind.Boolean),
            CanonicalKind.Int8 => Simple(CanonicalTypeKind.Int8) with { IsUnsigned = unsigned },
            CanonicalKind.Int16 => Simple(CanonicalTypeKind.Int16) with { IsUnsigned = unsigned },
            CanonicalKind.Int32 => Simple(CanonicalTypeKind.Int32) with { IsUnsigned = unsigned },
            CanonicalKind.Int64 => Simple(CanonicalTypeKind.Int64) with { IsUnsigned = unsigned },
            CanonicalKind.Decimal => new(CanonicalTypeKind.Decimal, null, t.Precision, t.Scale, false, false, note),
            CanonicalKind.Float => Simple(CanonicalTypeKind.Float),
            CanonicalKind.Double => Simple(CanonicalTypeKind.Double),
            CanonicalKind.String => new(CanonicalTypeKind.String, t.Length, null, null, t.Unicode == true, t.Length is null, note),
            CanonicalKind.Binary => new(CanonicalTypeKind.Binary, t.Length, null, null, false, t.Length is null, note, IsFixed: t.Fixed == true),
            CanonicalKind.Date => Simple(CanonicalTypeKind.Date),
            CanonicalKind.Time => new(CanonicalTypeKind.Time, null, null, t.Scale, false, false, note),
            CanonicalKind.Timestamp => new(t.TimeZone == true ? CanonicalTypeKind.TimestampTz : CanonicalTypeKind.Timestamp, null, null, t.Scale, false, false, note),
            CanonicalKind.Guid => Simple(CanonicalTypeKind.Guid),
            CanonicalKind.Json => Simple(CanonicalTypeKind.Json),
            CanonicalKind.Xml => Simple(CanonicalTypeKind.Xml),
            CanonicalKind.Interval or CanonicalKind.List => Simple(CanonicalTypeKind.Unmappable) with
            {
                SourceNote = $"Sync does not replicate {t.Kind.ToString().ToLowerInvariant()} columns; leave the column out, or give it a transform that reads it as text",
            },
            _ => Simple(CanonicalTypeKind.Unmappable) with { SourceNote = note ?? $"no canonical equivalent of {t}" },
        };

        CanonicalType Simple(CanonicalTypeKind kind) => new(kind, null, null, null, false, false, note);
    }

    /// <summary>
    /// This type as the shared dialects' type. A string or binary with no length and not <see cref="IsMax"/> is unbounded there, as is a
    /// decimal with no precision, and a time with no scale is one with no stated precision; each engine's table then decides what to write.
    /// </summary>
    public Shared.CanonicalType ToShared() => Kind switch
    {
        CanonicalTypeKind.Boolean => Shared.CanonicalType.Of(CanonicalKind.Boolean),
        CanonicalTypeKind.Int8 => Integer(CanonicalKind.Int8),
        CanonicalTypeKind.Int16 => Integer(CanonicalKind.Int16),
        CanonicalTypeKind.Int32 => Integer(CanonicalKind.Int32),
        CanonicalTypeKind.Int64 => Integer(CanonicalKind.Int64),
        CanonicalTypeKind.Decimal => new(CanonicalKind.Decimal, Precision: Precision, Scale: Scale),
        CanonicalTypeKind.Float => Shared.CanonicalType.Of(CanonicalKind.Float),
        CanonicalTypeKind.Double => Shared.CanonicalType.Of(CanonicalKind.Double),
        CanonicalTypeKind.String => Shared.CanonicalType.String(IsMax ? null : Length, IsUnicode),
        CanonicalTypeKind.Binary => Shared.CanonicalType.Binary(IsMax ? null : Length, IsFixed),
        CanonicalTypeKind.Date => Shared.CanonicalType.Of(CanonicalKind.Date),
        CanonicalTypeKind.Time => Shared.CanonicalType.Time(Scale),
        CanonicalTypeKind.Timestamp => Shared.CanonicalType.Timestamp(Scale),
        CanonicalTypeKind.TimestampTz => Shared.CanonicalType.Timestamp(Scale, tz: true),
        CanonicalTypeKind.Guid => Shared.CanonicalType.Of(CanonicalKind.Guid),
        CanonicalTypeKind.Json => Shared.CanonicalType.Of(CanonicalKind.Json),
        CanonicalTypeKind.Xml => Shared.CanonicalType.Of(CanonicalKind.Xml),
        _ => Shared.CanonicalType.Unmappable,
    };

    private Shared.CanonicalType Integer(CanonicalKind kind) => new(kind, Unsigned: IsUnsigned ? true : null);
}

/// <param name="Sql">The DDL-ready type spec for the target dialect, e.g. <c>"varchar(50)"</c>,
/// <c>"numeric(18,2)"</c>. Never populated for <see cref="CanonicalTypeKind.Unmappable"/> — a caller
/// must check <see cref="CanonicalType.Kind"/> before rendering, the same way a provisioning plan
/// checks it before ever calling <c>RenderColumnType</c>.</param>
/// <param name="Fidelity">Null when the mapping is faithful. Otherwise a sentence naming exactly what
/// is lost — this is the output that stops a silent truncation, so it is a return value, not a doc
/// comment.</param>
public sealed record RenderedColumnType(string Sql, string? Fidelity);
