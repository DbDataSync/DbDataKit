using DbDataKit.Dialect;

namespace DbDataSync.Core.Sql;

/// <summary>
/// Sync's <see cref="CanonicalType"/> and the shared <see cref="DbDataKit.Dialect.CanonicalType"/> describe the same thing; the
/// shared dialects own the per-engine tables (what each native type maps to, and how faithfully), and this converts between the two shapes.
/// </summary>
internal static class SharedTypes
{
    public static CanonicalType ToSync(TypeMapping<DbDataKit.Dialect.CanonicalType> mapping)
    {
        if (!mapping.IsMapped || mapping.Result is not { } t)
            return new CanonicalType(CanonicalTypeKind.Unmappable, null, null, null, false, false, mapping.Note);

        var note = mapping.Fidelity == Fidelity.Exact ? null : mapping.Note;
        var tz = t.TimeZone == true;
        // Sync has no unsigned integers: the next wider signed type holds every value, and a 64-bit one becomes DECIMAL(20,0).
        if (t.Unsigned == true)
        {
            var widened = Join(note, "Unsigned in the source; widened to the next signed type so every value fits.");
            switch (t.Kind)
            {
                case CanonicalKind.Int8: return Simple(CanonicalTypeKind.Int16, widened);
                case CanonicalKind.Int16: return Simple(CanonicalTypeKind.Int32, widened);
                case CanonicalKind.Int32: return Simple(CanonicalTypeKind.Int64, widened);
                case CanonicalKind.Int64: return new(CanonicalTypeKind.Decimal, null, 20, 0, false, false, widened);
            }
        }
        return t.Kind switch
        {
            CanonicalKind.Boolean => Simple(CanonicalTypeKind.Boolean, note),
            CanonicalKind.Int8 => Simple(CanonicalTypeKind.Int8, note),
            CanonicalKind.Int16 => Simple(CanonicalTypeKind.Int16, note),
            CanonicalKind.Int32 => Simple(CanonicalTypeKind.Int32, note),
            CanonicalKind.Int64 => Simple(CanonicalTypeKind.Int64, note),
            CanonicalKind.Decimal => new(CanonicalTypeKind.Decimal, null, t.Precision, t.Scale, false, false, note),
            CanonicalKind.Float => Simple(CanonicalTypeKind.Float, note),
            CanonicalKind.Double => Simple(CanonicalTypeKind.Double, note),
            CanonicalKind.String => new(CanonicalTypeKind.String, t.Length, null, null, t.Unicode == true, t.Length is null, note),
            CanonicalKind.Binary => new(CanonicalTypeKind.Binary, t.Length, null, null, false, t.Length is null, note),
            CanonicalKind.Date => Simple(CanonicalTypeKind.Date, note),
            CanonicalKind.Time => new(CanonicalTypeKind.Time, null, null, t.Scale, false, false, note),
            CanonicalKind.Timestamp => new(tz ? CanonicalTypeKind.TimestampTz : CanonicalTypeKind.Timestamp, null, null, t.Scale, false, false, note),
            CanonicalKind.Guid => Simple(CanonicalTypeKind.Guid, note),
            CanonicalKind.Json => Simple(CanonicalTypeKind.Json, note),
            CanonicalKind.Xml => Simple(CanonicalTypeKind.Xml, note),
            _ => new(CanonicalTypeKind.Unmappable, null, null, null, false, false, note ?? $"no Sync equivalent of {t}"),
        };
    }

    public static RenderedColumnType Render(ISqlDialect dialect, CanonicalType type)
    {
        var shared = ToShared(type);
        var mapping = dialect.ColumnType(shared);
        var native = mapping.Require();
        var fidelity = mapping.Fidelity == Fidelity.Exact ? null : mapping.Note;
        return new RenderedColumnType(native, Join(type.SourceNote, fidelity));
    }

    /// <summary>A time or timestamp with no stated precision means microseconds to Sync, where the shared tables' default is whole seconds.</summary>
    public static CanonicalType MicrosecondsByDefault(CanonicalType type) =>
        type is { Kind: CanonicalTypeKind.Time or CanonicalTypeKind.Timestamp or CanonicalTypeKind.TimestampTz, Scale: null } ? type with { Scale = 6 } : type;

    public static DbDataKit.Dialect.CanonicalType ToShared(CanonicalType type) => type.Kind switch
    {
        CanonicalTypeKind.Boolean => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Boolean),
        CanonicalTypeKind.Int8 => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Int8),
        CanonicalTypeKind.Int16 => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Int16),
        CanonicalTypeKind.Int32 => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Int32),
        CanonicalTypeKind.Int64 => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Int64),
        CanonicalTypeKind.Decimal => DbDataKit.Dialect.CanonicalType.Decimal(type.Precision ?? 18, type.Scale ?? 0),
        CanonicalTypeKind.Float => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Float),
        CanonicalTypeKind.Double => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Double),
        CanonicalTypeKind.String => DbDataKit.Dialect.CanonicalType.String(type.IsMax ? null : type.Length ?? 255, type.IsUnicode),
        CanonicalTypeKind.Binary => DbDataKit.Dialect.CanonicalType.Binary(type.IsMax ? null : type.Length ?? 255),
        CanonicalTypeKind.Date => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Date),
        CanonicalTypeKind.Time => DbDataKit.Dialect.CanonicalType.Time(type.Scale),
        CanonicalTypeKind.Timestamp => DbDataKit.Dialect.CanonicalType.Timestamp(type.Scale),
        CanonicalTypeKind.TimestampTz => DbDataKit.Dialect.CanonicalType.Timestamp(type.Scale, tz: true),
        CanonicalTypeKind.Guid => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Guid),
        CanonicalTypeKind.Json => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Json),
        CanonicalTypeKind.Xml => DbDataKit.Dialect.CanonicalType.Of(CanonicalKind.Xml),
        _ => DbDataKit.Dialect.CanonicalType.Unmappable,
    };

    private static CanonicalType Simple(CanonicalTypeKind kind, string? note) => new(kind, null, null, null, false, false, note);

    private static string? Join(string? a, string? b) => a is null ? b : b is null || a == b ? a : $"{a} {b}";
}
