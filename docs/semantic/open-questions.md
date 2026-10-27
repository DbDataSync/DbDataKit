# Open questions and concerns

Where the source documents disagree with each other or with the engine, where a rule is incomplete, and what needs
a decision. Each item says what is at stake and gives a recommendation to react to. The engine is a straw man: the
quickest way to settle most of these is to build the recommended option and look at the results.

## Decisions needed

| # | Question | Recommendation |
| --- | --- | --- |
| 1 | Does the engine take ad hoc tables as already-extracted rows with descriptors? | **Done**: yes. Extraction is a separate spec and doesn't block combining. |
| 2 | Are transforms computed in DuckDB over the result, or compiled into each native query? | **Done**: DuckDB first ([below](#where-transforms-run)). |
| 3 | Can two ad hoc tables that map to the same catalog dimension share a bridge? | **Done**: one bridge per ad hoc dimension, so cardinality is checked against one table's rows. |
| 4 | Should `blank` spread follow the draft (value on total rows only)? | **Done**: implemented as the draft says ([below](#blank-spread)). |
| 5 | Which of `x_filtered` or `{measure, filters}` is the filtered-measure form? | **Done**: `{measure, as, filters}`, one measure-entry shape for all overrides. |
| 6 | `ir_version` or `spec_version`? | `spec_version`, as the other specs use. |

## Levels and spread

The Overview tab assigns spread finer (`repeat`, `blank`) to level 4 and rollup coarser to level 3. The engine
runs spread at level 2, because a request that mixes a regional budget with product-level sales cannot be answered
at all without it, and every level below 4 would otherwise return a partial result for the most common
cross-source query.

**Recommendation:** keep spread at level 2, and move filtered measures to level 2 as well, since they are
single-source. Update the Overview table. Related: the design document says that below its level the engine returns
the steps in `remaining` and never attempts them; today the engine's level setting does gate this, but there is no
test that a level 1 engine refuses a level 2 step beyond fixtures `level1/04` and `level1/05`. **Proposed:** add one fixture per
step type that proves the gate.

## Blank spread

**Resolved in the engine.** Three sources disagreed:

| Source | `blank` means |
| --- | --- |
| Concepts draft | Detail rows are empty; the value appears only on a total row that is at the measure's native grain |
| Engine before | An extra row per coarser value, with NULL in the missing dimension and the value in the measure |
| Mashup spec | A NULL group is rejected (`union_attribute_not_shared`) |

The engine now follows the draft. Without total rows a `blank` measure has no values and the result says so in a
`spread_blank` warning (fixtures `level2/03` and `level2/05`); with totals it appears on the rows at or above its
native grain (fixture `level3/03`). Still open: whether a result that is empty because nothing could be shown
should be `complete` (as now, with a warning) or `partial`.

## Additivity and rollup

The design document puts `x_additivity` (additive, semi-additive, non-additive) on every measure and descriptor.
The draft and engine use per-dimension `rollup` rules with `sum`, `last`, `none` and the rest, and the engine
reads only `{default: sum}` or `{default: none}`.

**Recommendation:** rollup rules are the single source of truth and additivity is derived from them
([catalog.md](catalog.md#measure-semantics)). Keeping both invites contradictions such as a measure marked additive
whose rule is `none`. **Done:** the engine writes `x_additivity` into descriptors as a computed field, derived from the rules
(additive if all are `sum`, non-additive if the default is `none`, semi-additive otherwise), and views read it.

Work in the engine: read per-dimension rules and `first`/`last`; implement the `last` semantics (last period in
scope for the whole group); validate rules against the dimension kinds.

## Totals and non-additive measures

Three statements were not quite consistent:

- Native query: totals are "computed by the source for every measure, including non-additive ones".
- Mashup: totals may be produced in DuckDB for additive measures and must be refused for non-additive ones.
- Concepts draft: totals follow the rollup rules; `none` means recompute at the source.

**Implemented as one rule.** A total row for a measure comes from its rollup rule. Rules computable from detail rows
(`sum`, `min`, `max`, `avg`, `first`, `last`) roll up in DuckDB; `none` (distinct counts, ratios) asks the source
again at the coarser grain, so a cross-source query can total any measure exactly (fixture `level3/01`, on SQL
Server too). A repeated measure's subtotal is read at its own grain. No `total_not_computable` refusal is needed,
because every source can recompute at a grain it can already group by. The cost, one extra source query per grouping
set, is removed by native subtotals: one `ROLLUP` query per result set ([combine.md](combine.md#native-subtotals)). Totals use
a single `_rollup_level` column, with the meaning of each level in the descriptor. Where they are computed (`native`, `engine`, `auto`) is a
setting: a request hint, then the source's `totals`, then the engine default, which is `native`; whether `auto` should be the default is open.

## Totals on both axes of a matrix

**Decided and implemented.** A matrix with a total row and a total column needs a `CUBE`-style set of groupings, which a chain (`ROLLUP`) can't give. `totals` takes `cube: true`
(at most five dimensions); the level column stays a single number, numbered by how many dimensions are rolled up and then by position, and the descriptor's level map says what each is
([combine.md](combine.md#totals), fixtures `level3/34` and `35`).

## Naming audit

A pass over the specs, schemas, catalog and error codes for names that disagree, and what was decided.

**Applied**

| Change | Detail |
| --- | --- |
| Descriptor field kind is `x_kind` | `x_role` now means only a role-playing role on a relationship. The view error is `wrong_field_kind` |
| Snake case for every name we define | `time_attributes` and `date_range` (both since gone), and the operators `not_equals`, `not_set`, `not_contains`, `in_date_range`, `before_date`, `after_date` (which then became one `between`, with `lt` and `gt` on dates). The old names are not accepted (nothing has shipped). The descriptor's `primaryKey` stays camel because it is Frictionless's |
| `x_` only on extensions of a standard | Dropped inside our own documents, the request and the result: `totals`, `post_filters`, `transform`, `assumptions`, `resolved`, `having`. Kept in the descriptor (Frictionless) and the catalog's relationship fields (Ossie): `x_totals`, `x_transform` (a descriptor field), `x_semantics`, `x_binding`, `x_provenance`, `x_role`, `x_source` |
| A binding's native expression | `native: {DAX: ...}`, the same key as on a measure, in place of a lower-case `dax:` |
| `totals.rollup` is `totals.subtotal_by` | In the request, the descriptor's `x_totals`, and the `totals` step. `rollup` now means only the aggregation rule |
| `range.n` is `range.count` | Then `range` went away: a relative period is an operator on a period attribute (below) |
| A calendar's `offsets` are `shifts` | Matching `period` and the `prior_period` transform. A period's signed `offset` keeps its name |
| Error codes read `unknown_<thing>` and `<thing>_missing` | `unknown_role`, `unknown_period_unit`, `unknown_rollup_attribute`; `having_measure_missing`, `order_member_missing`, `totals_missing`, `totals_member_missing`, `transform_member_missing`. The `*_unavailable` and `*_not_*` codes already say different things and stay |

**Decided against:** renaming "level" and "unit" (they read fine in context); adding a model segment to names (the SQL-like format resolves its own names to ours);
renaming a result's `datasets` (now `result_sets`) to `scans`. `source` stays wherever it names where something in the model comes from, including a dataset's `source`, which is Ossie's own field.

**Left as they are** (no preference either way, so no change): the catalog's own additions (`semantics`, `synonyms`, `roles`, `format`, `nonempty`, `members`, `hierarchies`, `segments`, `calendars`) carry no prefix; the
`$defs` names inside the JSON Schemas are camel case but internal.

## Terminology

**Done.** [overview.md](overview.md#terminology) defines the words: a dimension is a table, an attribute is one of its columns, a fact table has fact rows, and a measure aggregates a column of one. The renames that followed from it:

| Where | Before | Now |
| --- | --- | --- |
| Request | `dimensions: ["date.fiscal_month"]` | `attributes` |
| Catalog | a flat `dimensions` block whose keys were attributes | `dimensions` of dimensions, each with `default_role` once and an `attributes` map; a binding no longer names a role |
| Descriptor | `x_kind: dimension` | `attribute` for a groupable column, `column` for one that can't be grouped |
| Errors | `unknown_rollup_dimension`, `unreachable_dimension` and the like | `unknown_rollup_attribute`, `unreachable_attribute`, ... |
| Physical layer | Ossie's `datasets` and `fields`; bindings and `of` as `{dataset, field}`; `unknown_dataset`, `unknown_field`, `field_expression_*`, `measure_dataset_source_mismatch` | `tables` and `columns`; `{table, column}`; `unknown_table`, `unknown_column`, `column_expression_*`, `measure_table_source_mismatch`; `is_time: true` on a column. Export and import translate to and from Ossie's words (`OssieShape`) |
| Result | `datasets` in the result envelope, `Dataset` in C#, `expect.datasets` in fixtures | `result_sets`, `ResultSet`, `expect.result_sets`: the rows a query returns are result sets, and "table" is kept for the catalog. A descriptor's `schema.fields` stays, since it is the Frictionless Table Schema's word |
| Request | `{"transform": {"as", "type", ...}}` | `{"as", "transform": <kind>, ...}`: a transform entry is one flat object |
| Request | `{"as", "transform": "period_to_date", "measure", "unit"}` | `{"as", "transform": "window", "measure", "along", "start", "end"}`: year to date is `start: {period_start: date.fiscal_year}`, a rolling twelve months is `start: -11`; the filter operator `period_to_date` is unchanged. Codes `period_to_date_too_many_periods` and `period_to_date_totals_unsupported` are `window_too_many_periods` and `window_totals_unsupported`; new `window_frame_invalid`, `window_period_not_coarser`, `window_attribute_not_coarser` |
| Internals | `dims`, `dim`, `timeDim` for attributes | `attrs`, `attr`, `timeAttr`; `dimension` where the table is meant |
| Engine | `DimensionDef` for a column | `AttributeDef` for the column, `DimensionDef` for the table |
| Filters | `member` (an attribute or a measure's value) | `attribute`, or `measure` for a condition on a measure's value. In our terms a member is a value of an attribute, so `member` was the wrong word for a name |
| Time | `time_attributes` with `granularity`, `date_range` and `range` | Filters on a period attribute: `last_complete` (with `count`), `current`, `period_to_date`, `between`, `equals`; the breakdown is the attribute in `attributes`. New codes: `period_operator_not_period_attribute`, `period_count_invalid`, `period_operator_takes_values`; `date_range_invalid` is `between_invalid`. |

Still open: the name `member` in `memberName` and the `unknown_member` family of codes (they mean a measure or an attribute by name), the operator names (`last_complete`, `current`, `period_to_date`), which are working names.

## Naming and versions

- **`ir_version` and `x_filtered`** (native query tab, v0.1) against **`spec_version: 0.3`** and
  `{measure, filters}` (the repository's schema, which follows the later documents). Pick one set of names.
- **Result step names.** Done: `rollup` and `spread` replace `regrain` in the schema, engine, fixtures, README and
  warning code (`spread_blank`). Today's step always spreads (a coarser result set moved to the requested finer grain);
  `rollup` is in the schema and not yet emitted. The Overview tab of the design document still says "regrain" and
  needs the same edit.
- **Spec versions.** Extraction, mapping and mashup are `0.2`, native query is `0.1`, the engine emits `0.3`. **No decision yet** (no strong opinion; revisit at the integration).
  **Proposed:** one version per document shipped from this repo, bumped together, until the specs have separate
  release cycles.
- **Request hashes** are stable within one implementation only; number formatting can differ between serializers.
  If hashes must be shared across implementations, define canonical number formatting. **No decision yet**; it only matters once a second implementation exists.

## Filtered measures on joined dimensions

**Resolved.** A measure filter may use any dimension available in the measure's source, including joined ones reachable
many-to-one from its fact table, because the SQL emitter joins what it needs and DAX's `KEEPFILTERS` works over related
columns. Fixture `level2/07` filters on the fact's own `status` column; filters through a join need a fixture.

## Where transforms run

**Decided: in DuckDB over the combined rows.** `pct_of_total`, `share_of_parent`, `per_entity`, `running_total` and `rank` are
implemented that way ([query.md](query.md#transforms)). One template per transform can't disagree with itself across dialects,
and it is testable now. Compiling them into each native query (a window function in T-SQL, `REMOVEFILTERS` patterns in DAX) is
a later optimization, to be verified against the same fixtures. `prior_period` is the exception that needs more than a window:
it needs a shifted scan through the catalog's calendar (see [Calendars](#calendars)), which is implemented.

## Transform gaps

What the transforms can't say yet, to review one at a time (see [query.md](query.md#transforms)):

| Gap | Status |
| --- | --- |
| Rolling and trailing windows, year to date, a month and the next | **Done**: the `window` transform |
| Difference from the previous row (`lag`), and the next row (`lead`), without a calendar shift | **Done**: `window` with `offset` (`-1` lag, `1` lead) and `output` (`value`, `delta`, `pct_change`) |
| A cumulative share (running total as a share of the total) | **Done**: chained transforms, a transform reads an earlier one's `as` ([query.md](query.md#chaining-transforms)) |
| Top N / bottom N directly | **Done**: `rank` takes `keep` (and `ties: break` for exactly N rows) |
| Index to a base period (100 at a chosen member) | **Done**: the `index` transform, a separate transform with its own base scan ([query.md](query.md#index-to-a-base-member)). Open: a `base: first` (the first member of the result) |
| A named derived measure as a transform's base | **Settled**: `rank` reads one today; `pct_of_total` and `running_total` can't because a ratio doesn't add up; `prior_period`, `window` and `per_entity` can't because they scan the source again. Chaining is the way to build on a computed value |
| An "Other" bucket: the rows beyond the top N rolled into one row so the total still adds up | Open: `keep` drops them; an `other` option on `keep` would need to roll the rest up with the measure's rollup rule |

## DAX testing

Today SSAS sources run through a DuckDB stand-in that executes an equivalent SQL query, and the engine reports the
DAX text. That tests the engine's logic but not the DAX. Options, in rising cost:

1. **Text snapshots** of the emitted DAX (cheap, catches accidental changes, proves nothing about results).
2. **Equivalence stand-in** (today): DuckDB runs the semantically equivalent SQL. Proves the plan, not the DAX.
3. **A Windows runner with SSAS Tabular** loaded with the sample data, running the emitted DAX against it.
4. **A Power BI or Fabric semantic model** reached over an XMLA endpoint.

**Recommendation:** options 1 and 2 until the emitters are stable, then option 3 or 4 for a nightly cross-backend
reconciliation, comparing result sets within a tolerance. Until then T-SQL on SQL Server and DuckDB are the two
native formats under test, as the repository does now.

**Investigated** ([ssas.md](ssas.md)): option 3 needs Windows for the client as well as the server (ADOMD.NET throws `NotSupportedException` on Linux, checked), Windows containers aren't a route, and
a hosted `windows-2022` runner can install Tabular and run the suite (checked: all 365 tests pass on real DAX, after fixing a misspelt `ROLLUPADDISSUBTOTAL` and keeping groups that have facts). The repository now has a self-contained Tabular model (`ssas/model.tmsl.json`, data inside the model, checked against the seed), `Qis.Ssas`'s executor, a
`QIS_SSAS` switch in the conformance suite, a manual workflow that does all of this on a hosted runner, and a fallback plan for a Windows VM with a self-hosted runner. Option 4 would need an XMLA client written for Linux and a paid capacity.

## Cube coupling

The native query tab compiles SQL through the Cube schema compiler and keeps the request Cube-compatible, which
makes the query format and the compiler's internal API a dependency. The engine in this repository emits SQL
directly. **Recommendation:** treat Cube compatibility as optional. Keep the Cube field names where they are
natural (`measures`, `dimensions`, `filters`, `order`, `limit`) so a Cube front end remains
possible, but do not require Cube's compiler. If the open questions about Cube multi-stage measures on the MSSQL
dialect are not answered, they become moot.

## The completion parser

The design document says escape-hatch SQL must parse under `sqlglot`. There is no equivalent in .NET, so the
engine uses DuckDB's own parser (`json_serialize_sql`) plus the locked-down execution. The risk is that DuckDB's
serialized JSON shape is not a stable API and must be pinned and tested per DuckDB version. Fixture
`level1/10` and the sandbox cases cover the current shape. **Recommendation:** accept this and add a sandbox test
suite (CTEs, table functions inside subqueries, qualified names, multiple statements, non-SELECT) that runs on every
DuckDB upgrade. Update the document text to say "DuckDB's parser".

## Plan: written by whom?

The Mashup tab shows `kind: plan` documents with steps and a `combine` list as if an agent writes them. The
Concepts draft says the agent writes only semantic requests, the planner generates the plan, and the plan is
inspectable ("explain") but never written by the agent. These cannot both be true.

**Decided:** agents do not write the plan. The agent writes a semantic request; the planner produces the plan; the plan
is stored in the run record and shown in an explain view. A structured plan the agent writes would duplicate the
request's job and give a second thing to validate. The completion escape hatch remains the only native code an
agent writes. If a hand-written plan is wanted for power users, it can be accepted as a separate document kind
without changing the request.

## Ad hoc tables

- **Storage and size.** Where do registered rows live, and what is the row cap? In-session DuckDB tables for
  `session` scope; durable storage for `personal` and above. The cap should be the same row cap as any step.
- **Registration API.** A `kind: table` document ([combine.md](combine.md#registering-ad-hoc-tables)) is a start;
  the transport (tool call, upload, reference to a Graph item) is undecided.
- **Binding across several dimensions.** One column binding to several catalog dimensions (a "region-territory"
  code) is not covered; bindings are one-to-one so far.
- **Confirmation.** `confirmed_by: user` needs a place in the conversation flow; today nothing records it.
- **Fuzzy threshold.** 0.92 is a default from the document; it needs tuning against real territory and customer
  lists, and the fixtures should pin the exact scores at the boundary.

## Calendars

**SSAS source:** done for months, quarters and years. The sample Tabular model has the prior-year counterpart columns and a year of earlier budget rows, so `prior_period`, `window`, `index` and the whole-period operators run on it; the date table's order still comes from a SQL source. **Plan 19 J23:** the model's Date table is now at day grain (the budget relates to it on the month's first day), so the day-level operators apply to it (fixtures `level4/128` to `130`), and the calendar is read with DAX over that table when no SQL source has one (`CalendarFromDaxTests`; [ssas-gaps.md](ssas-gaps.md)).

Role-playing dimensions are **done** at the catalog and query level: each role is a dimension named `role.attribute`, the bare name reads the measure's role or the default ([catalog.md](catalog.md#tables-columns-and-relationships)). Calendars, `time_attributes`, `prior_period` and allocation weights work in any role ([catalog.md](catalog.md#calendars)). Not yet: a role-aware form in the SQL-like format being specified elsewhere.

**Partly resolved.** The 4-4-5 problem (a `DATEADD(YEAR)` shift agrees with a fiscal-year shift only for month-aligned calendars)
is avoided by making the counterpart *data*: the catalog's `calendars` name a dimension bound to a column holding each member's
counterpart one unit earlier ([catalog.md](catalog.md#calendars)), so whoever fills the date table decides what "a year ago" means.
`prior_period` and allocation weights from the previous period are implemented on it.

Row presence is decided: a period with an earlier value but no current one is dropped, in every emitter.

Dates and relative periods are **done** (`date` type; `last_complete`, `current`, `period_to_date` and `between` as filter operators on a period attribute, replacing the old `time_attributes` and `range`; see [query.md](query.md#relative-periods)). Whole
periods become member filters rather than date filters, so they reach sources without a date column and can be moved back a year.

A `between` on dates (and `period_to_date`) now moves back through a counterpart for each *day* in the date table ([query.md](query.md#relative-periods)).

Datetime and time zones are **done** for SQL sources ([query.md](query.md#datetime-and-time-zones)): a catalog and request zone, instants in results, whole-day filters, buckets from hour to year, and a datetime as its own calendar. A datetime can be tied to a date dimension, so fiscal periods and prior-period shifts work on it (a range of dates on the datetime can't be moved back). Not yet: SSAS (DAX has no zone conversion); and any zone arithmetic the source can't do (T-SQL needs Windows zone names, which `TimeZoneInfo` maps from IANA).

A window of members around each one (year to date at each month, the trailing three or twelve months, a month and the next) is **done** as the `window` transform, exact for distinct counts (it replaced the `period_to_date` transform). Offsets of more than one unit, and later periods, are **done** (`level4/53` to `55`). Filters other than `equals` on a shifted period dimension are **done** (`level4/61`), and `window` works in any role and on a datetime tied to a date dimension. Also, `week` and calendar `month` are just levels a catalog can add;
the sample catalog has only the fiscal ones.

### Period shifts

Decided: a calendar shift is written `period: {unit, offset}` on both `allocate` and `prior_period`, with a signed `offset` where negative
is earlier (`-1` is the previous period). The name is neutral because the same shape will carry later periods and to-date ranges. Zero
is invalid. Every other value is implemented: `-2` follows the calendar's counterparts twice, and a positive offset reads them the other way. The error for an unknown unit is `period_unit_unknown`.

## Ossie compatibility

The catalog extends Ossie heavily ([catalog.md](catalog.md#starting-point-apache-ossie)). Risks:

- Ossie is a draft (`0.2.0.dev0`); field names may change.
- Moving QIS blocks into `custom_extensions` as JSON strings is lossless but not pleasant to read. The authoring
  format avoids that cost.
- Ossie's one-model-per-document rule means the cross-source layer (logical dimensions and measures) has no home in
  Ossie. We keep it in QIS blocks.
- Dialects: only `OSSIE_SQL_2026` has defined semantics. T-SQL and DuckDB are not in the enum.

**Recommendation:** keep the authoring superset, write the Ossie export as a tested converter, and check each Ossie
release against it. Offer upstream the pieces that are generally useful (rollup rules, spread, native grain,
structured measures) if they turn out to be stable.

## Performance: loading result sets into DuckDB

**Loading: done (plan 19 J21, measured 2026-10-27).** `Sandbox.Run` used one parameterized `INSERT` per row: about 1.5 ms a row, so about 640 rows a second and no better with size (10,000 rows 15.8 s, 50,000 rows 80.6 s, 100,000 rows 154 s; the default row cap of 1,000,000 would have taken about 26 minutes). It now uses DuckDB's appender: 10,000 rows 0.14 s, 100,000 rows 0.43 s, 1,000,000 rows 1.0 s (about 1,000,000 rows a second, a 1,500-fold gain at 1M), with the same results (the whole suite, the goldens and the engine fixtures pass unchanged). `SandboxLoadBenchmark` measures it (`DBDATA_BENCH=1`, `DBDATA_BENCH_MAX_ROWS`).

**Reading rows from a source is now the larger cost, and is not fixed.** `QueryRowReader` reads a row at a time through ADO.NET: 1,000,000 rows take about 5 seconds from DuckDB (about 200,000 rows a second; the type lookup per cell was not the cause, the driver's per-value read is). The next step is the one this section always named: an Arrow path (an Arrow Flight stream from a source that offers one, or Arrow built from the driver's reader) registered in DuckDB without materializing a dictionary per row, and the same seam for a pass-through run (DuckDB reading a source's emitted native query through its own extension). Decisions needed: the Arrow type mapping (instants as UTC timestamps, decimals), row caps and timeouts on the bulk path, and keeping runs replayable (snapshotting the rows). A result set is also held as `List<Dictionary<string, object?>>` and written to the run record as JSON, so a very large result costs memory at three points (the rows read, the rows appended, the record), which an Arrow path would also shrink.

**Arrow read path: started (plan 19 J24, measured 2026-10-27, Release build).** `QueryRowReader.ReadColumnar` reads a command into one Arrow record batch with typed getters (`Int64`, `Double`, `Decimal128`, `Boolean`, and text for strings and dates; dates stay ISO text as in the row form): 1,000,000 rows from DuckDB take 1.8 s against 4.1 s (555,000 against 244,000 rows a second). `ColumnarRows.ToRows()` gives the rows the row read gives (a test compares them). A column type with no Arrow form here (instants, binary, GUIDs) makes the read fall back to rows on the same reader, never run twice. A decimal takes the provider's precision and scale where it reports them (SQL Server), otherwise the largest scale among the values (DuckDB.NET), so no value rounds; a decimal then reads back with that scale (12.5 as 12.5000), which is numerically equal. `ConnectionsSourceExecutor` implements `IColumnarSourceExecutor`, and `Sandbox.RunColumnar` appends from the arrays; the load itself is no faster (the appender already runs at about 1,000,000 rows a second), so the gain is in the read and in not holding a dictionary per row. **The engine uses it:** a source scan from an executor that reads Arrow is a `ResultSet` that holds `Columnar` and builds `Rows` on first use; `Sandbox.Run` loads such a set from its columns. Calendar and dimension reads, ad hoc tables and native batches still read rows. Every fixture runs on the columnar stand-in and on a rows-only executor. Still open: the type decisions above (instants as UTC timestamps, caps and timeouts on the bulk path, replay), the run record (it still writes rows as JSON, which materializes them), and SQL Server's path, covered only by the engine suites in CI.

## Engine work implied by these docs

In dependency order:

1. ~~Rename `regrain` to `rollup`/`spread`.~~ Done.
2. ~~Fix `blank` spread to the draft's meaning.~~ Done.
3. ~~Add JSON Schemas for the catalog, the descriptor, and each `remaining` step; validate them in tests.~~ Done, with negative tests.
4. ~~Read per-dimension rollup rules; add `first` and `last`.~~ Done (used by totals; fixtures for `first`/`last` still to add).
5. ~~Move to the Ossie-shaped catalog and add the catalog validation codes.~~ Done, with the export, calendars, snowflake joins, role-playing dimensions and several fact tables per source. Derived measures (`compute`), segments and the Ossie import (a lossless round trip of an export) done, and importing a plain Ossie model (inferred dimensions and measures, with a list of what was left out).
6. ~~Table registry, bindings, catalog value maps, bridge~~ Done, with fuzzy and llm maps. `allocate` done (by a weight measure, equal shares, or the previous period's weights). The explicit `join` stage isn't needed yet: joins happen inside bridges.
7. ~~Filtered measures, aliases and spread overrides.~~ Done. ~~Transforms in DuckDB~~ Done, with `prior_period` and offset allocation on calendars. ~~Rollup overrides (`empty`, `acknowledge`).~~ Done. Relative ranges and a `date` type done.
8. ~~Totals.~~ Done. ~~Fuzzy and LLM value maps.~~ Done. ~~`allocate`~~ Done.
9. ~~`clarify` and `unsupported` kinds.~~ Done.
10. ~~Shadow mode for completions that overlap engine-executed steps.~~ Done.

## Known gaps in the ad hoc slice

- **Replay** is resolved: a run record carries the setup documents and replays in a new session (a test checks every fixture).
- **Scope enforcement.** Scopes are stored, and a catalog map must be shared or governed, but promotion rules and
  visibility are not enforced.
- **Members** are resolved: a dimension's `members` can be a list, or `{from: source}` to read its dimension table, so a member
  with no fact rows can be a map target ([catalog.md](catalog.md#dimensions)). A dimension with no setting still takes its members
  from the fact rows.
- **One key per bindings document.** Several catalog dimensions from one table (region and product) need several
  documents, or a change to the format.
- **Weighted relations** are implemented for ad hoc dimensions. The request-level `allocate` (by a weight measure or equal shares) is implemented too. Weights from an earlier period work through the calendar.
