using DbDataKit.Dialect;

namespace DbDataKit.Sync.Core.Sql;

/// <summary>How a built-in dialect reads and writes a column type: through the shared table of its engine, with nothing of Sync's own on top.</summary>
internal static class DialectTypes
{
    /// <summary>The shared table's reading; a kind Sync does not replicate (an interval, a list) is unmappable, with a note that says what to do.</summary>
    public static TypeMapping<CanonicalType> Read(ISqlDialect shared, string nativeType)
    {
        var read = shared.ToCanonical(nativeType);
        return read.Result is { Kind: CanonicalKind.Interval or CanonicalKind.List } t
            ? TypeMapping<CanonicalType>.Unmappable(
                $"Sync does not replicate {t.Kind.ToString().ToLowerInvariant()} columns ({nativeType.Trim()}); leave the column out, or give it a transform that reads it as text")
            : read;
    }

    /// <summary>The shared table's DDL for <paramref name="type"/>, its note joined to <paramref name="sourceNote"/>.</summary>
    public static RenderedColumnType Write(ISqlDialect shared, CanonicalType type, string? sourceNote)
    {
        var mapping = shared.ColumnType(type);
        var native = mapping.Require();
        return new RenderedColumnType(native, Join(sourceNote, mapping.Fidelity == Fidelity.Exact ? null : mapping.Note));
    }

    public static string? Join(string? a, string? b) => a is null ? b : b is null || a == b ? a : $"{a} {b}";
}
