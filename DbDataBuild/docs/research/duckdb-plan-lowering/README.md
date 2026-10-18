# Research: lowering DuckDB model SQL through DuckDB's binder (round 1: 2026-09-30; round 2 below: 2026-10-01)

**Question.** Can DuckDB expand things that need its own binder (macros, PIVOT with runtime values, `SELECT *`, `COLUMNS()`) before we transpile through polyglot, so we gain those features without implementing or gating each one?

**Status.** Research only. Nothing in `src/` uses this. The code here is the prototype, kept as evidence. DuckDB CLI 1.5.4, polyglot 0.13.1.

## Findings

| Hook | Result |
|---|---|
| `json_serialize_sql` / `json_deserialize_sql` | Parser AST only, SELECT only. Macros are not expanded and PIVOT is not desugared. Round-tripping gains nothing. |
| `json_serialize_plan(sql, optimize := false)` | Bound logical plan with macros, defaults, PIVOT, `*` and implicit casts expanded. Serialize-only: DuckDB has no plan-to-SQL function. All 93 spike queries bind and serialize. |
| Macros | Expanded only at bind. The catalog (`duckdb_functions()`) holds body text and parameter names, but not defaults. |
| Dynamic PIVOT | Needs data at bind time. On an empty table it silently yields no pivot columns (dangerous for our empty-table describe sessions). The statement form (`PIVOT t ON ...`) failed in `json_serialize_plan` (not investigated); polyglot passes it verbatim to T-SQL, which is wrong. |
| Seeding PIVOT values | `getvariable()` cannot feed `PIVOT ... IN`. An **enum type** works: `IN yr` with `CREATE TYPE yr AS ENUM (...)` needs no data. `query()` also does bind-time dynamic SQL. |
| Plan to SQL (prototype) | `unparse.py` turns the unoptimized plan into core SQL. Result-equal to DuckDB on the 6 simple cases tried, and on enum-seeded PIVOT. Handles `LOGICAL_GET`, `PROJECTION`, `FILTER`, `AGGREGATE_AND_GROUP_BY` only. |

Operators across the 93 spike cases (`surface.py`; the numbers differ by one for PROJECTION and GET from an earlier hand run): PROJECTION 103, GET 98, FILTER 19, DUMMY_SCAN 9, COMPARISON_JOIN 8, AGGREGATE_AND_GROUP_BY 6, WINDOW 5, DISTINCT 5, LIMIT 3, ORDER_BY 3, MATERIALIZED_CTE 3, CTE_REF 3, CROSS_PRODUCT 3, UNION 2, DELIM_JOIN 2, DELIM_GET 2, UNNEST/SAMPLE/INTERSECT/EXCEPT 1 each. A full unparser needs about 20 operator kinds. Correlated subqueries (DELIM_JOIN), CTE references and windows are the hard ones.

## Risks

- The plan JSON is internal. I found no documented stability guarantee (the docs fetch returned only a redirect page, so absence is unconfirmed). Pin DuckDB and test the unparser against it.
- Internal function names leak into the plan (`count_star`, `~~`, `sum_no_overflow`, `list_value`, `struct_pack`, `to_days`) and need mapping back to surface SQL.
- Output is noisy (explicit casts, `IS NOT DISTINCT FROM` filters) and harder to review than the author's SQL.
- The optimized plan must not be used: it reorders and rewrites.
- If the lowered SQL replaced the author's SQL, linter diagnostics would lose source spans.

## Proposed design (not adopted)

1. Author's SQL stays the source of truth; the matrix linter runs on it as today.
2. Optional lowering (`DuckDB bind, then unparse to core SQL`) only for constructs the matrix marks `lowerable`: macros, table macros, PIVOT/UNPIVOT, star variants, `COLUMNS()`. An unsupported operator raises a diagnostic and falls back to current behavior.
3. PIVOT requires seeded values (enum type or values declared in model YAML). Unseeded dynamic PIVOT is a hard error, never an empty result.
4. Macros come from project-supplied macro files loaded into the describe session, which also supplies defaults.
5. Lowered SQL is shown in `explain` and written to `rendered/`.
6. A differential test: original and lowered queries return equal results on DuckDB.

Suggested order: enum-seeded PIVOT and macros first, then joins, windows and delim joins.

## Files

- `setup.sql`: schema `staging` (`cities`, `wide`), scalar macro `net(a, tax := 0.2)`, table macro `big_cities(min_pop)`, enum `yr`.
- `unparse.py`: plan-to-SQL prototype (`python3 unparse.py`, needs the `duckdb` CLI).
- `tree.py`: prints a plan tree.
- `surface.py`: operator, expression and function tally over `spike/constructs.yml`.

---

# Round 2 (2026-10-01): can the plan be the source of all target SQL?

**The operator's questions.** Look further into plan lowering, especially if it gives better type metadata and queries that correct for differences in behavior; generating every target query from the plan is acceptable if the result stays reasonably readable and reasonably similar to the source query.

**Short answer: yes, and the evidence is better than round 1 suggested.** A prototype that turns DuckDB's bound, unoptimized plan into one readable `SELECT` lowered 81 of the 93 spike constructs to a query whose result equals the original's in DuckDB. Executed through polyglot on real engines, the lowered form matched DuckDB in **8 more cases on SQL Server (63 to 71) and 2 more on PostgreSQL (74 to 76), with no regressions**, before any target-specific rule exists. Ten of the 11 cases that did not lower are constructs the matrix already treats as hard (correlated subqueries, `UNNEST`, `USING SAMPLE`, `DISTINCT ON`, `LIMIT ... PERCENT`, nested types); the eleventh is an artifact of how my test extracted the SQL.

All numbers below come from the code in this directory (`lower.py`, `run_corpus.py`, `make_lowered_corpus.py`, `compare_runs.py`, `probe_types.py`, `frame_probe.py`; the raw results are `corpus-results.json`, `lowered-cases.yml` and `engine-comparison.txt`). The engines were SQL Server 2022 and PostgreSQL 17 in local containers, DuckDB 1.5.4. The spike runner got an environment override for the two engine passwords (`SPIKE_MSSQL_PASSWORD`, `SPIKE_PG_PASSWORD`) so it can use the throwaway test containers.

## 1. The plan types every expression

Probing (`probe_types.py`) shows every node of the bound plan carries its exact result type, and the binder has already made DuckDB's implicit conversions explicit:

| Source | Bound plan says | Consequence |
|---|---|---|
| `a / b` (INTEGER) | `/` returns DOUBLE; both operands wrapped in `CAST(... AS DOUBLE)` | The silent integer-division bug is **fixed by construction**: the lowered query says `CAST(a AS DOUBLE) / CAST(b AS DOUBLE)` and every engine divides as floats |
| `avg(a)` (INTEGER) | aggregate returns DOUBLE, argument is INTEGER | A rule can pin it: `avg(CAST(a AS DOUBLE))` (T-SQL's `AVG` over integers truncates) |
| `sum(a)` (INTEGER) | returns HUGEINT | T-SQL returns INT and can overflow: a rule has to choose a wider target type, which needs a decision |
| `n * n`, `n + 1` (DECIMAL(10,2)) | DECIMAL(18,4), DECIMAL(13,2) | the exact result precision is known, so it can be pinned with a cast where engines differ |
| `date_trunc('month', DATE)`, `DATE + INTERVAL` | TIMESTAMP | T-SQL returns DATE; pinned with `CAST(... AS TIMESTAMP)` |
| `try_cast`, `LIKE`, `ILIKE` | `try_cast: true`; functions `~~` and `~~*` | the exact operation is known, no guessing from text |
| every output column | a type | `define` can read column types from the plan (today it uses `DESCRIBE`), including DECIMAL width and scale |

This also answers an open item from the spike (RESULTS.md, item 6): **column-to-column string comparisons cannot be found from the AST because it has no types**. In the plan every comparison has typed operands, so `s1 = s2` on two VARCHAR columns is found like `s = 'x'`.

## 2. The lowering prototype

`lower.py` builds a `Rel` (a SELECT block under construction) per operator and merges operators into their child's block when SQL allows (projection over filter over join is one `SELECT ... FROM ... JOIN ... WHERE`), otherwise wraps the child as a subquery. It covers get, projection, filter, aggregate (group by, having), order, limit, distinct, comparison and cross joins (inner, left, right, full), window (frames, lead and lag), union, intersect, except, dummy scan and CTEs, with about 25 expression kinds. Internal names are mapped back to SQL (`~~` is `LIKE`, `count_star` is `count(*)`, an interval constructor back to `INTERVAL 3 DAY`), constants the binder widened are printed as plain literals, and a column is qualified only where a block has more than one source.

Results over the 93-case spike corpus (`run_corpus.py`; "equal" means the lowered query returns the same multiset of rows as the original in DuckDB):

| | count |
|---|---|
| lowered, result equal | 81 (one more differs only by the clock: `CURRENT_TIMESTAMP`) |
| not lowered | 11: correlated subqueries (2 `DELIM_JOIN`, 1 `MARK` join), `UNNEST`, `USING SAMPLE`, `DISTINCT ON`, `LIMIT ... PERCENT` and a limit that is not a constant (2), list and struct constructors (2), and one case my test extraction mangled |

**Readability and similarity.** Token similarity of lowered to source (case-insensitive, parentheses and `AS` ignored): median 0.98, mean 0.83, 53 of 81 at 0.8 or above. The low scores are almost all one thing: `SELECT *` becoming an explicit column list, which is what the design wants anyway (explicit columns everywhere). The rest are binder normalizations the author did not write:

| Source | Lowered |
|---|---|
| `SELECT * FROM t LEFT JOIN u USING (a)` | `SELECT t.id, t.a, ..., u.b AS b_2 FROM t LEFT JOIN u ON (t.a = u.a)` |
| `a BETWEEN 2 AND 5` | `(a >= 2) AND (a <= 5)` |
| `NULLIF(a, 0)` | `CASE WHEN (a = 0) THEN CAST(NULL AS INTEGER) ELSE a END` |
| `s \|\| NULL` | `CAST(NULL AS INTEGER) AS col1` (the binder folded the constant) |
| `SELECT a, COUNT(*) ... GROUP BY ALL` | `SELECT a, count(*) AS "count_star()" ... GROUP BY a` (the alias pins DuckDB's output name) |
| `QUALIFY row_number() ... = 1` | a subquery with the window, filtered outside |

I judge these acceptable for reading ("reasonably similar"), with one caveat the operator should decide on: **constant folding hides what the author wrote** (the `s || NULL` row), and the binder rewrites `NOT (a > 1)` as `a <= 1`. Both are semantically identical but make a diff against the source noisier. Two things came out of reading the output that I got wrong the first time and fixed, both worth remembering as the kind of bug a hand-written emitter has: a `ROWS ... CURRENT ROW` window frame was printed as the default `RANGE` frame (the two differ when rows tie; `frame_probe.py` now checks this row by row), and `TRUE` arrived as `CAST('t' AS BOOLEAN)`, which SQL Server rejected.

## 3. Behavior on real engines (`engine-comparison.txt`)

The lowered form of each of the 81 cases was executed through the spike runner (DuckDB oracle, polyglot transpile, execute) and compared with the original's result:

| | original | lowered | improved | regressed |
|---|---|---|---|---|
| SQL Server 2022 (MATCH of 81) | 63 | 71 | 8 | 0 |
| PostgreSQL 17 (MATCH of 81) | 74 | 76 | 2 | 0 |

- Fixed by the binder expanding a construct polyglot passes through wrongly: `GROUP BY ALL`, `GROUP BY 1`, `JOIN ... USING`, `NATURAL JOIN` (five cases on SQL Server, two on PostgreSQL).
- Fixed by two type-driven rules written against the plan (about ten lines each): pin `avg` of a non-DOUBLE argument to `avg(CAST(x AS DOUBLE))`, and pin DATE-to-TIMESTAMP widening with an explicit cast (`avg_int`, `avg_decimal`, `date_trunc_month`, `date_add_interval`).
- Still wrong, and why none of it is a lowering failure: string comparison, `IN`, `LIKE` and `REPLACE` under a case-insensitive collation, `LENGTH` ignoring trailing spaces, `TRY_CAST` on PostgreSQL, `ROUND(double, n)` on PostgreSQL, regex on SQL Server 2022. These need rules that emit **target-specific** syntax (a `COLLATE` clause, a different function), and the lowered text must still run in DuckDB for the differential tests. See the proposal below.

## 4. Options

| | A. All target SQL from the plan | B. Author's AST plus a type oracle from the plan | C. Hybrid (recommended) |
|---|---|---|---|
| Idea | the lowered query is the only thing polyglot sees | keep the author's text and its AST; ask DuckDB for the types of subexpressions and apply rewrites at AST nodes | lower only what needs the binder or needs types; everything else stays as written |
| Similarity to the source | good for most, binder normalizations show | best | best where it matters |
| Correct by construction for division, star, USING, GROUP BY ALL, macros, PIVOT | yes | no (rewrites by hand) | yes for lowered constructs |
| Knows the type of every operand | yes | only by aligning AST nodes with plan nodes, which has no source positions | yes where lowered |
| Cost | unparser of about 20 operators and 25 expression kinds, plus a function map | node alignment is the hard part | the same unparser, used on demand |
| Main risk | plan JSON is not a stable API; correlated subqueries and `UNNEST` have no lowering yet | alignment errors are silent | two code paths |

**Recommendation: A, introduced as C.** Concretely:
1. Make lowering a stage **before** the matrix lint and polyglot: author's SQL, then bound plan, then core SQL (DuckDB dialect, explicit columns, explicit casts), then lint and transpile. Anything the unparser cannot lower is `DDB-3xx`, "cannot be lowered", and the matrix says what to rewrite. Nothing falls back silently (consistent with "no silent defaults").
2. Keep the lowered query as an artifact you can read: render it next to the loads (`rendered/lowered/<model>/lowered.sql`, committed, checked by `render --check`), so a reviewer sees exactly what runs and a diff shows what the binder changed.
3. **Marker macros for target-specific rules.** The plan tells us exactly where a string comparison, `LIKE`, `REPLACE` or `LENGTH` over VARCHAR is. The lowering stage can wrap those operands in identity macros defined in DuckDB (`ddb_cs(x)` means `x`), so the canonical query still runs unchanged in DuckDB for the differential tests, and the target stage replaces each marker with the engine's own form (`x COLLATE Latin1_General_100_CS_AS`, `LEN(x + 'x') - 1`). Type-pinning rules (avg, date to timestamp, sum widening, decimal precision) need no markers because they are plain casts. This is a proposal; it is not built or tested here.
4. Use the plan for **metadata** regardless: output column types (including HUGEINT for `sum`, DECIMAL width and scale), inferred nullability stays with the analyzer, and typed operands for the lint.
5. Seed values for dynamic `PIVOT` and project macro files, as round 1 recommended, come after the unparser exists.

## 5. What it would take, and what could go wrong

- **Build**: a C# port of `lower.py` (the structure is small: `Rel`, `expr`, `window`, operators), plus the remaining operators (correlated subqueries as `EXISTS` or joins, `UNNEST`, `SAMPLE`, `DISTINCT ON`, percent limits, nested constructors). The test strategy is the one that already exists: every lowered case differentially tested against the author's query on seeded data, plus golden files of the lowered text, plus a row-order check for queries with `ORDER BY`/`LIMIT` (`run_corpus.py` compares sorted multisets, which hides order differences; `frame_probe.py` shows the row-by-row form).
- **Plan JSON stability** is the real risk: the format is internal. Pin the DuckDB version per project, keep golden plan fixtures, and fail closed: an operator or expression kind the unparser does not know is a refusal, never a guess. I did not find any statement of stability in DuckDB's documentation (the docs fetch returned only a redirect page).
- **Unoptimized plan only.** The prototype uses `optimize := false`; the target's own optimizer decides execution, so lowering does not change performance in principle.
- **Macros and PIVOT** are lowered by the binder for free, but need project macro files loaded into the describe session and seeded values; round 1 covers those.
- **What lowering cannot fix**: collation and trailing-space semantics, `TRY_CAST` on PostgreSQL, function availability (regex on older SQL Server). Those stay matrix and profile decisions; lowering only makes the places they apply exact.

## 6. Decisions for the operator

1. Is the lowered query a **committed artifact** next to `rendered/` (my recommendation, for review and diffs), or an internal intermediate?
2. Is "cannot be lowered" a **hard error** (my recommendation) or may the tool fall back to transpiling the author's SQL directly with matrix gating?
3. Is the binder's normalization (BETWEEN expanded, NULLIF as CASE, folded constants) acceptable in the committed lowered form?

## 7. Reproducing

```
cd docs/research/duckdb-plan-lowering
python3 probe_types.py                 # what types the bound plan carries
python3 run_corpus.py                  # lower the 93 spike cases, check result equality and similarity (writes corpus-results.json)
python3 frame_probe.py                 # window frame fidelity, row by row
python3 make_lowered_corpus.py <dir>   # writes <dir>/spike/constructs.yml of the lowered cases
# then, with the test engines up (scripts/test-engines.sh up):
SPIKE_MSSQL_PASSWORD=... SPIKE_PG_PASSWORD=... dotnet run --project spike/DbDataBuild.Spike -- diff <dir> <mssql host:port> <pg host:port> > lowered.md
python3 compare_runs.py spike/results.sqlserver2022.generated.md lowered.md
```

---

# Status (2026-10-02): built

The operator accepted the three decisions of section 6 (the lowered query is a committed artifact, "cannot be lowered" is a hard error, and the binder's rewrites are acceptable). Lowering is now a stage of the tool; see `docs/progress/state-and-apply.md` entry 20 and DESIGN.md section 7.6. Differences from the prototype that building it settled:

- After fixing a limit-parsing slip in the prototype (`OFFSET` unset was read as a non-constant limit), 83 of the 93 spike constructs lower result-equal and 9 are refused (list and struct constructors, `UNNEST`, `USING SAMPLE`, `DISTINCT ON`, correlated subqueries, and one case my extraction mangled). The C# lowerer is tested against the same corpus, and also compares output column names.
- **Output names come from `DESCRIBE`, not the plan**: a query whose plan has no projection at the top (a bare aggregate) loses its author's aliases in the plan, which the end-to-end test caught (`AS size` became `col1`). Names are applied to the lowered query by position.
- **Null ordering is always written** in `ORDER BY` and window ordering (`x NULLS LAST`), because DuckDB, SQL Server and PostgreSQL disagree about where NULLs sort and the plan states it explicitly.
- The string-collation marker macros of section 4 are still a proposal, not built.

## Update (2026-10-02): subqueries

The plan shapes are in `subq_probe.py`. `EXISTS`/`NOT EXISTS`/`IN`/`NOT IN`/scalar/`LATERAL` all arrive as `DELIM_JOIN` (types `MARK`, `SINGLE`, `INNER`, `LEFT`) with the right side reading the outer values through `DELIM_GET`; uncorrelated `IN` is a `MARK` comparison join, uncorrelated scalar and `EXISTS` are crossed one-row subqueries guarded by `CASE WHEN count(*) > 1 THEN error(...) ELSE first(x) END` and by `count(*) = 1` over `LIMIT 1`. The lowerer reverses all of it (see DESIGN.md section 7.6). 53 subquery forms lower to queries with the same rows in DuckDB, and run on real engines: 51 match on SQL Server and 52 on PostgreSQL. The two differences are an engine limitation (SQL Server rejects `sum((SELECT ...))`) and the case-insensitive collation of the spike database (`s IN (SELECT ...)` over strings that differ only by case), neither a lowering problem.
