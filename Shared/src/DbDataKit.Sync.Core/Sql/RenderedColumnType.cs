namespace DbDataKit.Sync.Core.Sql;

/// <param name="Sql">The DDL-ready type spec for the target dialect, e.g. <c>"varchar(50)"</c>,
/// <c>"numeric(18,2)"</c>. Never populated for an unmappable type: a caller checks the type's kind before
/// writing it, the same way a provisioning plan does before it calls <see cref="SqlDialect.WriteColumnType"/>.</param>
/// <param name="Fidelity">Null when the mapping is faithful. Otherwise a sentence naming exactly what
/// is lost — this is the output that stops a silent truncation, so it is a return value, not a doc
/// comment.</param>
public sealed record RenderedColumnType(string Sql, string? Fidelity);
