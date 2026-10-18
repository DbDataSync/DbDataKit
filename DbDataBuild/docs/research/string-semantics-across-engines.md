# String comparison across engines: what the profile guarantees

Status: **parked** (2026-10-06, owner): the review below and the pieces built from it (a per-connection setting, DDB-236, `trimmed`, the DuckDB-side emulation) were patches; the owner wants one design of the whole before anything more is added (`OPEN-ITEMS.md` section P lists what exists and the questions a design must answer). Read this note as the record of how it came to be, not as the plan.

## What the setting is

`string_semantics` (`DESIGN.md` 7.4) declares how strings compare: `case`, `accent`, `trailing_space`, and a collation per engine. Since 2026-10-06 a connection may say its own (`connections.<name>.string_semantics`), each field it leaves out being the project's. A project that builds on SQL Server and PostgreSQL together needs that: SQL Server ignores trailing spaces in `=` whatever the collation, PostgreSQL always keeps them, so no single `trailing_space` is true of both. Before the override, one of the two engines failed DDB-310 whichever value was chosen.

## What the review found

The design text (7.4, "How the profile is applied") describes five mechanisms. What exists:

| Mechanism | State |
|---|---|
| The declared profile, in every header | built |
| Collation checks against the profile, offline (`validate`) and against the live catalog (`check`) | built (now per connection) |
| Collations stated explicitly in all generated DDL (and in the shape hash) | built |
| Target rules in the lowerer (`LEN` keeps trailing spaces on SQL Server, ...) | built for the rules listed in `DESIGN.md` 7.6.1 |
| Matrix rows `str.eq`, `str.like.*`, `str.len`, ... | present, `approximated` on the engines |
| DuckDB-side emulation of the target (`default_collation`, the `rtrim()` rewrite) for `sample` and `test` | **not built** |
| Any rewrite of a comparison on an engine that cannot satisfy the profile natively | **not built** |

So the profile is a **declaration that is checked**, not a behavior the tool imposes. A comparison runs with the engine's own rules. If a model runs on two connections whose profiles differ, `=`, `IN`, join keys, `GROUP BY`, `DISTINCT` and `UNION` over strings can return different rows, with no error on either side. The tool's headline promise (the answer is DuckDB's answer on every engine) therefore does not hold for strings across engines that disagree; it holds where the profile is the same and the engine can satisfy it.

## Ways to make connections agree (1 and 4 built 2026-10-06)

1. **Keep the data free of the difference. (Built.)** A data test that no value has trailing spaces (or that the column is `trimmed`), so `=` agrees on every engine. Cheapest; says what it covers and nothing else; fits the rule that the tool never reads values unasked if the test is a user's.
2. **Rewrite the comparison on the engine that cannot match.** `rtrim()` both sides on PostgreSQL to get `ignored`; append a sentinel to both sides on SQL Server (`x + '|'`) to get `significant`. Applies to `=`, `IN`, join keys, `GROUP BY`, `DISTINCT`, set operations and window partitions. Correct, but defeats index use on the wrapped column, and `LIKE`, `LEN` and ordering need their own rules.
3. **Build the DuckDB-side emulation. (Built.)** `sample` and `test` then show what the profile means (an `rtrim()` rewrite where the profile says ignored). It does not fix an engine; it makes the offline answer match the declared profile so a difference is visible before a run.
4. **A lint that names the models whose result depends on the difference. (Built.)** From the AST the linter already finds every string comparison; with two profiles in one project it can say which models run on connections that disagree.

A reasonable order: 1 and 4 first (small, honest), then 3, with 2 only where a project needs one model to mean the same thing on both engines and accepts the index cost.

## What is not settled

- Whether a profile per **folder** is wanted as well as per connection (a folder is where a model's meaning is usually decided).
- Whether a model that runs on connections with different profiles should be an error, a warning, or allowed silently (today: allowed silently).
- Case and accent have the same shape of problem (a case-sensitive collation on one engine, insensitive on another); the per-connection setting covers them the same way.

## Built on 2026-10-06

- `trimmed: true` on a column of a model or a mapped model: the declaration that no value ends in a space. `check` counts the rows that break it per connection (DDB-237; `DATALENGTH` and `length`, a count only).
- DDB-236, advice per model: a model on connections that compare strings differently, using string columns (by declared type, from the lowered query's column uses) in a filter, join, group, having, partition,
  distinct or set operation (case, accent, trailing space) or an order (case, accent). When only trailing spaces differ, columns declared `trimmed` do not count. `lint_ignore: [DDB-236]` silences it.
- Checked on SQL Server and PostgreSQL: the counts (`StringProfileConformanceTests`); the lint is offline (`StringProfileLintTests`).

## Still open

- Option 3 is built (below). Not covered by it: set operations without ALL, `count(DISTINCT x)` and `LIKE`.
- Option 2 (rewriting comparisons on the engine that cannot match): not built, and only worth it for a model that must mean the same on both engines and can pay the index cost.
- The lint reads column uses, not types of expressions: `lower(a) = b` is seen as a use of `a` and `b`.

## The DuckDB-side emulation (built 2026-10-06)

`sample` and `test` run each model the way its connection compares strings:

- **Case and accent**: `SET default_collation = '<the profile's duckdb collation>'` around the model's query only (`RESET` after). Observed in DuckDB 1.5.4: it reaches comparisons, joins, `GROUP BY` and `IN` at bind time; a table created under it keeps plain `VARCHAR` columns (so a test's exact comparison of expected and actual rows is not affected); it does **not** reach `count(DISTINCT x)`.
- **Trailing spaces** (`trailing_space: ignored`): no DuckDB setting does this, so the query is lowered and `rtrim()` is put on string operands of comparisons, `IN`, `BETWEEN`, join conditions, window partitions, `GROUP BY` keys and `DISTINCT`. A model that cannot be lowered is run as written, with a note.
- **Whose profile**: the model's first connection. A model on connections that disagree is run as the first one compares (DDB-236 is the lint for that case).
- **Checked against the engines**: the same three rows (`a`, `A`, `a `) and collation give the same counts of groups, of a self-join and of rows equal to `'a'` in DuckDB under the emulation and on SQL Server (case-insensitive and binary collations) and PostgreSQL (`C`).
- **Not covered**: set operations without ALL, `count(DISTINCT x)`, `LIKE` (SQL Server keeps trailing spaces there too), ordering (a trailing space does not decide an order), and any comparison inside a native model's text (it is not lowered).
