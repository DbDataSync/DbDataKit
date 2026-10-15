# DbDataKit.Dialect.Abstractions

Contract package for Plan 05. No dependencies. Everything else (`DbDataKit.Dialect`, Arrow mapping, conformance) builds on it.

## Public contract
- `EngineId` (normalised lower-case string), `EngineVersion` (comparable, tolerant `TryParse`), `EngineCaps` flags (capability queries instead of `engine == "x"`).
- `CanonicalType(Kind, Length, Precision, Scale, Unicode, TimeZone, Unsigned)` with a canonical text form (`string(unicode,50)`, `decimal(18,2)`, `timestamp(tz,6)`, `int64(unsigned)`) that round-trips through `Parse`/`ToString`. Null length on String/Binary means unbounded.
- `TypeMapping<T>(Result, Fidelity, Note)`; `Fidelity` is `Exact | Widened | Lossy | Unmappable`. Any non-Exact outcome carries a note; nothing coerces silently.
- `ISqlDialect`: `Quote`/`QuoteQualified` (always double the closer), quote-aware `SplitQualifiedName`, `IsSafeUnquoted`/`ValidateName`, `ParameterStyle`/`ParameterName`, `RowLimit(LimitOffset|OffsetFetch|TopN, tieSafe)` returning a `RowLimitFragment` (select prefix + suffix), `Literal(value, CanonicalType)`, `ColumnType` (canonical to native), `ToCanonical` (native to canonical), `Identifiers` rules, `Syntax` lexical flags and `ReadOnly` deny lists.

## Decisions
- Timestamp-with-zone is `Timestamp` + `TimeZone = true` (no separate kind); `Guid` is the one uuid kind.
- `Interval` and `List` are opaque; `Unmappable` is a kind so a plan can report the column rather than guess.
- Syntax flags (`SqlSyntax`) live here so the lexer, literal escaping and quoting agree on one description of an engine.
- The row-limit style is chosen by the caller; a dialect lists what it supports (`SupportedRowLimitStyles`) and rejects the rest. `tieSafe` is honoured only where the engine can do WITH TIES; the returned fragment says whether it was.
