# Plan 05: SQL Dialect and Type System (`DbDataKit.Dialect`)

Status: proposed. Depends on: none (Plan 01 for YAML descriptors). Foundation for Plans 04, 06, 07.
This is the highest-overlap area in the review; do it early.

## 1. Problem

| Concern | Sync | Proxy | Build |
|---|---|---|---|
| Dialect abstraction | `Core/Sql/SqlDialect` + `MsSql/Postgres/MySql/Oracle/DuckDb/JdbcDialect`; `Descriptor/DescriptorDialect` (YAML); `State/*StateDialect` (a third hierarchy) | `ParameterStyle` per provider; `GetSqlInfo` facts; `OdbcIdentifierQuote`/`IdentifierQuote` | `ITarget` (build), `DialectProfile` record as data (origin), `TrackingDdl.For(engine)` (state), ad hoc `engine == "postgres"` branches |
| Quoting | doubles the closer, quote-aware `SplitQualifiedName` | two implementations + `StandardQuote`, raw name when driver reports none | four implementations (`DialectProfile.Quote`, `TrackingDdl.Quote`, `DdlGenerator`, inline `[..]` in `MutationGate`) |
| Types | `CanonicalType` (Boolean, Int8..64, Decimal, Float, Double, String, Binary, Date, Time, Timestamp, TimestampTz, Guid, Json, Xml, Unmappable) with `RenderedColumnType` fidelity notes; hand-copied type maps in descriptor YAML vs C# | ADO.NET `GetFieldType` to Arrow in `ArrowColumnWriter` (hardcoded closures) | logical DuckDB type names; per-target DDL in `Ddl.cs`; `TransferValues`, `ClrType/PgType`, `LogicalTypes`, `DialectProfile.Types` (5 places) |
| SQL lexing | n/a | `SqlMarkers.Scan` (strings, `[ ]`, comments; no dollar-quote/backtick/`#`) | `ReadGuard.Strip` (misses Postgres `E'..\'..'`, backticks) |
| Read-only guard | n/a (Admin raw SQL by design) | none (S-1 High) | regex `ReadGuard` (F-3) |
| Row limits | `topN/limitOffset/offsetFetch`, tie-safe | n/a | limit style in `DialectProfile` |

## 2. Goals
1. One dialect model, data-driven where possible, with code overrides for structural differences.
2. One type system with explicit fidelity, round-trip-tested per engine.
3. One SQL lexer shared by parameter rewriting, guards and splitting.
4. One conformance harness so adding an engine means adding a descriptor plus passing tests.

Non-goals: transpiling SQL between dialects (Build's polyglot-sql/DuckDB lowering stays in Build; expose an `ISqlParser`/`ISqlTranspiler` seam only).

## 3. Design

### 3.1 Engine identity and capabilities
```csharp
readonly record struct EngineId(string Value);   // sqlserver, postgres, mysql, oracle, duckdb, fabric, snowflake, odbc:<generic>, flightsql, ...
record EngineVersion(int Major, int Minor = 0, int Patch = 0);
[Flags] enum EngineCaps { Merge, UpsertOnConflict, Returning, TransactionalDdl, TempTables, Schemas, BulkCopy,
                          MultiStatement, NamedParameters, PositionalParameters, TruncateTable, IdentityInsert, Sequences }
```
Capability queries replace `if (engine == ...)` branches (Build `Logins.cs`, `MutationGate.cs`, `DriverFailure.cs`).
Matrix-style data (Build's `matrix/strategies.yml`) can key off `EngineCaps`.

### 3.2 `ISqlDialect`
```
Quote(identifier) / QuoteQualified(parts)       always doubles the closing delimiter
SplitQualifiedName(string)                       quote-aware (from Sync)
IsSafeUnquoted(name) / ValidateName(name)        rejects control chars, unterminated quotes (Proxy S-9)
ParameterStyle: Named@ | Named: | Positional? | Numbered$1 ; ParameterName(i)
RowLimit(style: LimitOffset | OffsetFetch | TopN, tieSafe) -> SqlFragment
Literal(value, CanonicalType)                    N'' prefix for non-ASCII on T-SQL (Build TsqlLiterals), escaped otherwise
Ddl: CreateTable/AlterAddColumn/DropTable/CreateIndex fragments, ColumnType(CanonicalType) -> RenderedColumnType
Identifier rules: case folding, max length, reserved words, collation syntax
UseDatabase / session setup statements; ping statement; bulk/merge SQL builders live in Plan 07 and consume this
```
Rule retained from Sync's doc comment: the dialect handles "small mechanical differences"; structural
differences are separate strategies (Plan 07).

### 3.3 Data-first definition: `dialect.yaml`
Merge Sync's driver descriptor `dialect:` section, Build's `DialectProfile` and Proxy's static facts into one
schema (read with Plan 01 so it is strict and versioned):
```yaml
schema: dialect/1
id: sqlserver
extends: ansi
quote: { open: "[", close: "]" }
parameters: { style: named_at }
row_limit: { style: top_n, tie_safe_via: row_number }
identifier: { case: preserve, max_length: 128 }
capabilities: [merge, bulk_copy, temp_tables, transactional_ddl]
types:                       # native -> canonical (with (p,s) placeholders) and canonical -> native
  - { native: "nvarchar({n})", canonical: "string(unicode,{n})" }
```
`DialectProfile` records are loaded from this; compiled dialects (`SqlServerDialect`, `PostgresDialect`, ...)
subclass the data-driven base and override only what data cannot express (MERGE shape, Oracle quirks).
Curated dialects ship embedded; products and users can add project-level ones (Build's `dialects:` in
`dbdatabuild.yml`, Sync's `drivers/<id>/driver.yaml`) and `dialects.lock.yml` remains a discovered-facts file.

### 3.4 Canonical type model
Extend Sync's `CanonicalType` into a record that carries parameters:
`CanonicalType(Kind, Length?, Precision?, Scale?, Unicode?, TimeZone?, Unsigned?)` with kinds as in Sync plus Interval, Array/List
(opaque initially), Uuid (alias of Guid), Date32/Date64 distinctions only where an engine requires it. Mappers, all
bidirectional where meaningful and each returning `TypeMapping(Result, Fidelity {Exact, Widened, Lossy, Unmappable}, Note)`:

| Mapper | Source of truth today | Notes |
|---|---|---|
| Native <-> Canonical | per-dialect (Sync dialects; Build `DialectProfile.Types`/`Ddl.cs`) | one table per engine in `dialect.yaml` |
| Canonical <-> Arrow | Proxy `ArrowColumnWriter.Create` (if-chain) | make a data table; Decimal128 precision guard; document unsigned and tz choices (Timestamp us, UTC when tz-aware) |
| Canonical <-> DuckDB logical | Build `LogicalTypes`, `Define/*` | stays the model's declared vocabulary in Build; mapper lives with Build but uses the shared table |
| Canonical <-> CLR / DbType | Sync `GenericValueBinder`, `*ValueBinding`, Build `ClrType/PgType` | used by Plan 07 value conversion |

Golden table `types.golden.yml` lists canonical types x engines with expected native rendering and fidelity;
unit-tested offline and round-trip-tested against live engines in the conformance harness.

### 3.5 Shared SQL lexer (`SqlLexer`)
Tokenises at the lexical level only (no parsing): string literals (`'..'` with `''`, `E'..'` with backslash escapes, `N'..'`),
dollar-quoted strings (`$tag$..$tag$`), quoted identifiers (`"`, `[ ]`, backtick), comments (`--`, `/* */` including nesting where the
dialect nests, `#` for MySQL), parameter markers (`?`, `@name`, `:name`, `$1`), statement separators. Dialect flags choose which
forms apply. Consumers:
- Proxy `SqlMarkers.Scan` (`?` to `@pN`) and the missing dollar-quote/backtick/`#` cases;
- Plan 06 named-to-positional rewriter (fixes Sync's ODBC `@name` binding gap);
- read-only classifier (below), fixing Build's `E''` desync;
- multi-statement splitting for hooks/scripts.
Fuzz and property tests (round-trip: tokens re-concatenate to the input; no exception on random input), reusing Build's
`FuzzTests` approach.

### 3.6 Statement classifier / read-only guard (`SqlStatementClassifier`)
`Classify(sql, dialect) -> { Kind: Select|Insert|Update|Delete|Merge|Ddl|Dcl|Exec|Utility|Unknown, Statements, Findings }`
built on the lexer, with per-dialect deny lists beyond keywords (T-SQL `OPENROWSET/OPENQUERY/OPENDATASOURCE/xp_*`, Postgres
`pg_read_file/lo_import/dblink/pg_sleep/nextval`, DuckDB `read_csv/COPY/ATTACH/INSTALL/LOAD`). `ReadOnlyPolicy.Check(sql)` returns
allow/deny with a reason. An optional `ISqlParser` seam lets a product plug an AST-backed check (Build's polyglot-sql) for higher assurance.
Documented honestly as defence in depth: real enforcement is a read-only login/session (Plan 06 recipes). Used by Proxy (S-1),
Build (replaces `ReadGuard`), Sync (`query-preview`).

### 3.7 Conformance harness (`DbDataKit.Dialect.Conformance`)
xunit base classes parameterised by engine: quoting round-trips (hostile names), literal rendering, row limits incl. tie-safe,
type round-trip table, parameter marker rewriting, DDL smoke. Docker fixtures for SQL Server, Postgres, MySQL, Oracle (as available) and
in-process DuckDB. Adding an engine = adding a `dialect.yaml` + fixture + passing the suite.

## 4. Package layout
```
DbDataKit.Dialect.Abstractions   EngineId/Caps, ISqlDialect, CanonicalType, TypeMapping
DbDataKit.Dialect                data-driven dialect, built-in dialect.yaml set, lexer, classifier
DbDataKit.Dialect.Arrow          Canonical <-> Arrow mapping (depends on Apache.Arrow)
DbDataKit.Dialect.Conformance    test harness
```

## 5. Migration (strangler pattern, no big bang)
1. Build the shared lexer and quoting first (smallest, immediately fixes Build E-string and Proxy dollar-quote gaps).
2. Introduce `ISqlDialect` and make each product's existing dialect classes implement/adapt it; delete duplicates only after parity tests.
3. Move Sync's `CanonicalType`/mappers (they are the richest), then Build's `DialectProfile`, then Proxy's Arrow table.
4. Retire Sync's `State/*StateDialect` by having State use `ISqlDialect`; retire Build's `TrackingDdl.For(engine)` branches.

| Phase | Work | Exit |
|---|---|---|
| 1 | Lexer + quoting + hostile-name tests; Proxy and Build swap their scanners | Build `ReadGuard` E-string test passes |
| 2 | `ISqlDialect` + `dialect.yaml` loader; SQL Server and Postgres first | Both products pass existing suites on shared dialects |
| 3 | Canonical type model + golden table + live round trip | Round-trip CI job for SQL Server/Postgres |
| 4 | Classifier/ReadOnlyPolicy; Proxy enforces (S-1) | Hostile SQL tests |
| 5 | MySQL, Oracle, DuckDB, Fabric dialects; ODBC generic dialect from `SQLGetInfo` facts (Plan 04) | Conformance green per engine |
| 6 | Remove duplicates in the three products | `rg` shows one quoting implementation |

## 6. Risks
- Sync encodes dialect behaviour in C# overrides where Build encodes it in data; keep both paths available.
- Type fidelity decisions (Oracle NUMBER, SQL Server datetime2 precision, Postgres arrays) need explicit policy, not silent coercion.
- A lexer is easy to get subtly wrong; invest in tests before adoption.
- Avoid scope creep into parsing and transpilation.

## 7. Acceptance
One quoting implementation and one lexer used by all three products; canonical type golden table passes against live engines;
Proxy's read-only guard blocks the hostile corpus; adding a new engine requires no changes outside its dialect data and fixtures
(structural strategies excepted).
