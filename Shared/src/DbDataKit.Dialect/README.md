# DbDataKit.Dialect

Plan 05 implementation: data-driven dialects, the shared SQL lexer, the statement classifier and the read-only policy.
Depends on `DbDataKit.Dialect.Abstractions` and YamlDotNet (parser events only).

## Public contract
- `DialectCatalog.Builtin.Get("postgres")` returns an `ISqlDialect`. Built-ins (embedded `dialects/*.yaml`): `ansi`, `sqlserver`, `postgres`, `mysql`, `oracle`, `duckdb`, `fabric`.
  `catalog.With(yamlText, source)` adds a project-level dialect (may `extends` any built-in and inherits its compiled class). `OdbcDialect.FromFacts(OdbcFacts)` builds a generic `odbc:<name>` dialect from SQLGetInfo-like facts.
- `SqlLexer.Tokenize(sql, SqlSyntax)`: lexical only, never throws, tokens tile the input exactly (`SqlLexer.Join(tokens) == sql`). Strings (`''`, `E''`, `N''`, `$tag$`, Oracle `q'[ ]'`, MySQL backslash), quoted identifiers (`""`, `[]`, backtick), comments (`--`, `/* */` with a nesting flag, `#`, flagged `/*! */`), parameters (`?`, `@n`, `:n`, `$1`, `$n`), `;`.
  `SqlParameterRewriter.Rewrite` converts markers between styles without touching strings or comments.
- `SqlStatementClassifier.Classify` splits statements and classifies them (`Select|Insert|Update|Delete|Merge|Ddl|Dcl|Exec|Utility|Unknown`) and reports findings; `ReadOnlyPolicy.Check(sql, dialect, parser?)` allows exactly one SELECT without deny-listed words. `ISqlParser` is an optional AST seam that can only tighten the decision.

## dialect.yaml (schema `dialect/1`)
Keys: `schema id extends display quote parameters row_limit identifier reserved_words capabilities syntax read_only literals types types_prepend`.
List fields accept `X`, `X_add`, `X_remove`. Strict: unknown/duplicate keys, aliases, tags and bad enum values fail with `source:line`. Not depending on `DbDataKit.Config`; when it lands, the loader can sit behind its schema/version checks.
Type rows: `{ native: "varchar({n})", canonical: "string({n})", direction, fidelity, note, defaults, n_min/n_max/p_*/s_* }`. First match wins per direction. `{n}` length, `{p}` precision, `{s}` scale; a parenthesised group is optional when reading. Canonical pattern flags: `unicode`, `~unicode` (either), `tz`, `unsigned`, `fixed` (a row without `fixed` also writes a fixed binary). A Widened/Lossy row must carry a note. Defaults fill a missing precision/scale, never an unbounded length. Unknown native types are `Unmappable` with a note.
`types.golden.yml` (in the tests project) renders every canonical type per engine; regenerate with `UPDATE_GOLDEN=1`.

## Types with no stated size, unsigned integers and fixed-length binary

Every product that reads or writes a column type (Sync, Build, Transfer, the Gateway, the Store) gets these answers from the tables above.

- An unsigned integer keeps its `unsigned` flag. An engine with unsigned types writes them (MySQL `tinyint unsigned`, DuckDB `utinyint`, SQL Server
  `tinyint` for an unsigned 8-bit integer). An engine without them writes the next wider signed type with a note: a SQL Server `tinyint` becomes
  `smallint` on PostgreSQL, and a MySQL `bigint unsigned` becomes `numeric(20,0)`.
- A time or timestamp with no stated precision is written with microseconds (6 digits) on PostgreSQL, MySQL, DuckDB and Oracle, and with 7 digits on
  SQL Server. On MySQL that is `time(6)`, `datetime(6)` and `timestamp(6)`, so two changes in the same second keep different times. Reading MySQL's
  `datetime` with no precision gives `timestamp(0)`, MySQL's own default.
- A decimal with no stated precision (PostgreSQL's `numeric`) is written as PostgreSQL's `numeric`, which holds it exactly. Every other engine writes
  `decimal(38,9)` (`number(38,9)` on Oracle, `numeric(38,9)` for ANSI) with a note, so 12.75 stays 12.75; digits beyond 38, or beyond 9 after the
  point, are rounded.
- A fixed-length binary is `binary(fixed,n)`. SQL Server and MySQL read and write `binary(n)` as one (`binary(16)` stays `binary(16)`), and
  SQL Server's `rowversion` reads as `binary(fixed,8)` with a note that the engine no longer sets it. An engine with one binary type writes a fixed
  binary as that type: `bytea` on PostgreSQL, `raw(16)` on Oracle, `blob` on DuckDB. A fixed binary longer than the engine's fixed type
  (8000 bytes on SQL Server, 255 on MySQL) is written as variable-length binary.


## Decisions
- Compiled overrides are small: SQL Server (`#temp` names), Fabric, MySQL (trailing-space names), Oracle (no `"` in names). Everything else is data.
- Oracle: `NUMBER` unconstrained maps Lossy to unconstrained decimal; ints render `number(p)`; `DATE` reads as `timestamp(0)`; no TIME type (interval); boolean as `number(1)` (pre-23ai).
- SQL Server `tinyint` is unsigned; signed int8 widens to smallint. Unsigned 64-bit widens to `decimal(20,0)`.
- The classifier is defence in depth: it refuses harmless SQL that uses a deny word unquoted (a column named `load`); quote such names. Real enforcement is a read-only login (Plan 06).
- Postgres `?` is not a parameter marker (jsonb operator); callers pass `QuestionParameter` explicitly when rewriting.
- Limits: no T-SQL `GO`, no procedural bodies (`BEGIN..END` is lexed, not parsed), arrays/list types unmapped.
