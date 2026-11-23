using System.Data.Common;
using DbDataKit.Transfer;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// A rendered <see cref="BatchReloadSegment"/>: the SQL predicate limiting a statement to that
/// segment's rows, plus the parameters it references. Every bound value is a typed parameter — never
/// spliced into the text.
/// <para>
/// The predicate's *shape* — <c>1 = 1</c>, <c>IN (…)</c>, half-open bounds — is identical on every
/// engine, which is why it lives here. Only quoting and placeholder syntax vary, and those come from
/// the <see cref="SqlDialect"/>. Turning a bound into a typed parameter stays with the driver
/// (<see cref="ISegmentValueBinder"/>): that is where engines genuinely diverge, because a provider's
/// type enum has no engine-neutral equivalent.
/// </para>
/// </summary>
public sealed record SegmentScope(string Predicate, IReadOnlyList<DbParameter> Parameters)
{
    /// <summary>Matches every row. Used for <see cref="FullSegment"/> and for an unsegmented unit of
    /// work, so callers can always interpolate a predicate rather than conditionally emitting the
    /// whole WHERE clause.</summary>
    public static SegmentScope All { get; } = new("1 = 1", []);

    public void AddTo(DbCommand command)
    {
        foreach (var parameter in Parameters)
            command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Renders <paramref name="segment"/> against the table described by <paramref name="columns"/>
    /// (the source table for a reader, the target table for a writer — the segment column must exist
    /// on whichever side is being scoped).
    /// <para>
    /// Bound values arrive as strings from a web form or from system-computed bucket boundaries, so
    /// they're converted to the segment column's actual type and bound as parameters. This is
    /// deliberately stricter than <c>SourceTableRef.Filter</c>, which is a raw predicate an
    /// administrator hand-authors in config; segment bounds are ordinary user input.
    /// </para>
    /// </summary>
    /// <param name="columnMappings">
    /// Supplied by writers, omitted by readers — the dispatch this method uses to tell which is calling.
    /// A segment names a column on the *source* table, but a writer scopes the *target* — and a mapping
    /// is free to rename a column across the two. Passing the mappings translates the segment's column
    /// name to its target-side counterpart (matched on <see cref="ColumnMapping.Relationship"/> too, so a
    /// relationship's own column and a same-named primary one are never conflated — phase 195S), so a
    /// reload segmented on a renamed column scopes the same rows on both sides instead of failing to find
    /// the column (or, worse, finding an unrelated target column that happens to share the source name).
    /// A reader never translates — its segment already names a real column verbatim, on the primary
    /// source or, phase 195S, on a declared relationship — so it omits this and relies on
    /// <paramref name="relationshipColumns"/> instead when the segment is relationship-sourced.
    /// </param>
    /// <param name="reference">
    /// How the segmented column is written *in this statement*, fully resolved by the caller — bare
    /// <c>dialect.QuoteIdentifier</c> by default. A reader that joins the primary table under an alias
    /// (phase 187J's relationship joins), that has a transform to apply (phase 192S), or that segments by
    /// a relationship's own column (phase 195S) resolves and composes all of that itself — via
    /// <see cref="TransformAwareReference"/> composed with <see cref="SourceProjection.ReferenceFor"/> —
    /// before calling in; this method only ever calls the result once per bound. A writer never passes
    /// this at all: its predicate scopes the target's own real column, which needs none of the above.
    /// </param>
    /// <param name="relationshipColumns">
    /// One column list per declared relationship, keyed by <see cref="RelationshipConfig.Name"/>
    /// (ordinal-insensitive) — phase 195S, consulted instead of <paramref name="columns"/> for a segment
    /// whose own <c>Relationship</c> field names one, when reading (<paramref name="columnMappings"/> is
    /// null — see its own doc comment). Never consulted when writing: a writer's target has no
    /// relationships of its own, and the segment's relationship-sourced column has already been
    /// translated to a real target column by the time it gets there. Omitted (or missing the named
    /// relationship) while reading is a caller bug, not an operator-facing gap — the reader is expected
    /// to have already required that relationship's cache non-empty
    /// (<see cref="CachedMetadataLookup.RequireAll"/>) before calling in, the same way it already does
    /// for <paramref name="columns"/>.
    /// </param>
    public static SegmentScope Build(
        SqlDialect dialect,
        ISegmentValueBinder binder,
        BatchReloadSegment? segment,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMapping>? columnMappings = null,
        Func<string, string>? reference = null,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMetadata>>? relationshipColumns = null)
    {
        var resolved = Resolve(segment, columns, columnMappings, relationshipColumns);
        if (resolved.Filter is ScopeFilter.AllRows) return All;

        // The shared builder owns the predicate's shape and its placeholder text; binding each value stays here.
        var rendered = ScopedStatements.RenderScope(SharedStatementSyntax.For(dialect), resolved.Filter, reference);
        return new SegmentScope(rendered.Predicate, Bind(dialect, binder, rendered.Slots, resolved.Column));
    }

    /// <summary>
    /// A segment resolved against a table's columns: the structured <see cref="ScopeFilter"/> the shared statement builders
    /// render (<see cref="ScopedStatements"/>), and the column its values are compared with, which the caller needs to type
    /// the parameters (<see cref="Bind"/>). <see cref="Column"/> is null for a scope over every row.
    /// </summary>
    public sealed record ResolvedScope(ScopeFilter Filter, ColumnMetadata? Column);

    /// <summary>
    /// The first half of <see cref="Build"/>: resolves the segment's column (see the parameters of <see cref="Build"/>) and
    /// turns the segment into a <see cref="ScopeFilter"/>. Nothing is rendered or bound.
    /// </summary>
    public static ResolvedScope Resolve(
        BatchReloadSegment? segment,
        IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMapping>? columnMappings = null,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMetadata>>? relationshipColumns = null)
    {
        switch (segment)
        {
            case null or FullSegment:
                return new ResolvedScope(ScopeFilter.All, null);
            case ListSegment list:
            {
                var column = ResolveColumn(list.Column, list.Relationship, columns, columnMappings, relationshipColumns);
                return new ResolvedScope(ScopeFilter.List(column.Name, [.. list.Values]), column);
            }
            case RangeSegment range:
            {
                var column = ResolveColumn(range.Column, range.Relationship, columns, columnMappings, relationshipColumns);
                // Half-open: consecutive ranges tile a value space with no gap and no row processed twice.
                return new ResolvedScope(ScopeFilter.Range(column.Name, range.RangeMin, range.RangeMax), column);
            }
            case AutoSegment auto:
                throw new InvalidOperationException(
                    $"Auto segment on '{auto.Column}' reached execution unexpanded. Auto segments must be " +
                    "expanded into concrete ranges (ISegmentExpandingReader.ExpandAutoSegmentsAsync) when " +
                    "the work is enqueued, never carried through as a runtime segment.");
            default:
                throw new ArgumentOutOfRangeException(nameof(segment), segment, "Unknown segment mode.");
        }
    }

    // Every generated parameter name (seg0, seg1, ..., segMin, segMax, afterOrdinal, upToOrdinal) starts with a
    // letter, not an underscore — Oracle's bind-variable grammar rejects a leading underscore outright
    // (ORA-00911), confirmed by running this against a live server (phase 148). The names are the shared
    // builder's (ScopedStatements); this is where the caller turns them into typed parameters.

    /// <summary>
    /// Creates the typed parameters of a built statement's scope slots, in slot order: each value goes through the
    /// driver's <see cref="ISegmentValueBinder"/> against <paramref name="column"/>, named the way this dialect's
    /// provider wants a parameter name (<see cref="SqlDialect.ParameterName"/>). Ordinal slots are not scope slots; see
    /// <see cref="AddTo(DbCommand, SqlDialect, ISegmentValueBinder, IReadOnlyList{ParameterSlot}, ColumnMetadata?, long, long)"/>.
    /// </summary>
    public static IReadOnlyList<DbParameter> Bind(
        SqlDialect dialect, ISegmentValueBinder binder, IReadOnlyList<ParameterSlot> slots, ColumnMetadata? column) =>
        [.. slots
            .Where(slot => slot.Role is ParameterRole.ScopeValue or ParameterRole.ScopeMin or ParameterRole.ScopeMax)
            .Select(slot => binder.CreateParameter(
                dialect.ParameterName(slot.Name),
                (string)slot.Value!,
                column ?? throw new InvalidOperationException("a scope slot needs the column it is compared with")))];

    /// <summary>
    /// Adds every parameter of a built statement to <paramref name="command"/>, in slot order: scope slots typed by the
    /// binder, ordinal slots as 64-bit integers carrying <paramref name="after"/> and <paramref name="upTo"/>. The statement's
    /// text is built once; a chunked apply calls this once per chunk with that chunk's bounds.
    /// </summary>
    public static void AddTo(
        DbCommand command, SqlDialect dialect, ISegmentValueBinder binder, IReadOnlyList<ParameterSlot> slots,
        ColumnMetadata? column, long after = 0, long upTo = 0)
    {
        foreach (var slot in slots)
        {
            switch (slot.Role)
            {
                case ParameterRole.OrdinalAfter:
                    command.AddParameter(dialect.ParameterName(slot.Name), after);
                    break;
                case ParameterRole.OrdinalUpTo:
                    command.AddParameter(dialect.ParameterName(slot.Name), upTo);
                    break;
                default:
                    command.Parameters.Add(binder.CreateParameter(
                        dialect.ParameterName(slot.Name),
                        (string)slot.Value!,
                        column ?? throw new InvalidOperationException("a scope slot needs the column it is compared with")));
                    break;
            }
        }
    }

    /// <summary>
    /// Wraps <paramref name="reference"/> so that if <paramref name="columnName"/> (on
    /// <paramref name="relationship"/>, or the primary source when it's null) also happens to be a
    /// <see cref="ColumnMapping"/> carrying a <see cref="ColumnMapping.Transform"/>, the segment or
    /// watermark predicate renders through that transform too — exactly the expression
    /// <see cref="SourceProjection.Render"/> would emit for that column. This is what keeps a source-side
    /// predicate and the target's already-*written* value agreeing on what "the column" means: the target
    /// stores the transformed value, so a predicate comparing the raw one would silently compare two
    /// different value-spaces. No change is needed on a reconciling writer's own side — it already just
    /// reuses these bounds verbatim against the target, which is correct once the source filters in the
    /// same space the target stores. See phase 192S.
    /// <para>
    /// **Phase 195S**: <paramref name="relationship"/> generalizes what used to only ever match a
    /// primary-sourced <see cref="ColumnMapping"/> — a segment or watermark naming a relationship's own
    /// column now looks for a mapping sharing *that* relationship instead of none at all.
    /// </para>
    /// </summary>
    public static Func<string, string> TransformAwareReference(
        string columnName, IReadOnlyList<ColumnMapping>? columnMappings, Func<string, string> reference,
        string? relationship = null)
    {
        var transform = columnMappings?.FirstOrDefault(m =>
            string.Equals(m.Relationship, relationship, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.SourceColumn, columnName, StringComparison.OrdinalIgnoreCase))?.Transform;

        return transform is null ? reference : name => SourceProjection.RenderExpression(name, transform, reference);
    }

    private static ColumnMetadata ResolveColumn(
        string columnName, string? relationship, IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<ColumnMapping>? columnMappings,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMetadata>>? relationshipColumns)
    {
        // Writer mode: translate the segment's source-side name (relationship included, so a
        // relationship's own column is never conflated with a same-named primary one) to its
        // target-side counterpart, then look *that* up in the target's own single cache — a
        // relationship-sourced segment reaching a writer has already been written as a real target
        // column by the time it gets there (phase 192S's "no join or transform needed on the target"
        // ruling), so relationshipColumns is never consulted here.
        if (columnMappings is not null)
        {
            var resolvedName = columnMappings
                .FirstOrDefault(m => string.Equals(m.Relationship, relationship, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(m.SourceColumn, columnName, StringComparison.OrdinalIgnoreCase))?
                .TargetColumn ?? columnName;

            return columns.FirstOrDefault(c => string.Equals(c.Name, resolvedName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Segment column '{resolvedName}' was not found (available: {string.Join(", ", columns.Select(c => c.Name))}).");
        }

        // Reader mode: the segment names a real column directly — on the primary source, or, phase
        // 195S, on a declared relationship — with no translation involved.
        if (relationship is null)
            return columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Segment column '{columnName}' was not found (available: {string.Join(", ", columns.Select(c => c.Name))}).");

        var candidates = relationshipColumns is not null && relationshipColumns.TryGetValue(relationship, out var found)
            ? found
            : throw new InvalidOperationException(
                $"Segment/watermark column '{columnName}' names relationship '{relationship}', but no " +
                "cached column metadata was supplied for it — the caller is expected to have required " +
                "that relationship's cache non-empty before calling in, the same way it already does " +
                "for the primary source.");

        return candidates.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Segment/watermark column '{columnName}' was not found on relationship '{relationship}' " +
                $"(available: {string.Join(", ", candidates.Select(c => c.Name))}).");
    }
}

/// <summary>
/// Turns a segment's string bound into a parameter typed to match the column it is compared against.
/// Binding a bound as an untyped string would make the engine convert on the *column* side of the
/// comparison for anything non-textual, which both changes the comparison's semantics and prevents an
/// index seek — the opposite of what segment scoping exists to achieve.
/// <para>
/// Per-driver rather than on the dialect: the mapping's *shape* generalises, but its output is a
/// provider type enum (<c>SqlDbType</c>, <c>NpgsqlDbType</c>, …) with no common ancestor. Extract a
/// shared implementation only once two drivers demonstrably agree.
/// </para>
/// </summary>
public interface ISegmentValueBinder
{
    /// <param name="name">Already rendered through <see cref="SqlDialect.ParameterName"/>.</param>
    DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column);
}
