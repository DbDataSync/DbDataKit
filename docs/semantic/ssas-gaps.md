# What the SSAS (DAX) path lacks, and what was fixed

Reviewed 2026-10-27 (plan 19 J22) by reading `DaxEmitter`, `SsasExecutor` and the catalog loader against the SQL emitter, and by sending probes to a real Analysis Services (`SsasProbe`, workflow `ssas_probe`; results below are measured, not read). [ssas.md](ssas.md) has the earlier findings (runs 1 to 12).

## Fixed in J22

| Finding | Evidence | Fix |
| --- | --- | --- |
| **`not_equals`, `not_contains` and a `not_equals` on a measure kept a blank.** SQL's `<>` and `NOT LIKE` drop a NULL; DAX's `NOT ( col IN { ... } )` and `NOT CONTAINSSTRING` keep it | probes O1 and O3: with `West` made blank, `NOT ( [r] IN { "East" } )` returned the blank row. (A comparison such as `>` already dropped it, O2.) No sample fixture could see it: the model has no blank | the emitter writes `NOT ISBLANK ( col ) && ...` (probe O5 returned only `North`); fixtures `level2/28`, `29`, `30` run `not_equals`, `not_contains` and `starts_with` on the SSAS source, on the stand-in and on real DAX |
| **Agent-written native text ran as anything the command accepts.** `ExecuteNativeText` handed the text to ADOMD, which also runs MDX, DMX and DMV queries | probe P1: an MDX `SELECT ... FROM [Model]` returned the model's value through the same path | `SsasExecutor.IsDaxQuery`: only text that starts (after whitespace and comments) with `EVALUATE` or `DEFINE` is run; anything else is `NotSupportedException` before a connection is opened. `SsasExecutorGuardTests` |

## Not done: gaps, with what each needs

1. **Datetime attributes** are refused when the catalog loads (`datetime_unsupported_source`): DAX has no time zone conversion. SQL sources support instants, whole-day filters and buckets from hour to year. *Needs a decision:* a model that stores UTC with a zone table the emitter joins, or buckets precomputed in the model per zone, or leaving it unsupported.
2. **Day-level dates.** The sample model has no date column, so `period_to_date`, written-date `between`, and a `calendar_date` counterpart do not apply to the SSAS source (`filters_not_applied`). *Needs:* a `Date` column in `Date` of the model, a `native: {DAX: ...}` binding, fixtures. Small, and checkable in the SSAS job.
3. **Calendars are read from SQL sources only.** `BuildLevelRanges` and `BuildPairs` (the order and days of each period, hierarchy checks) are T-SQL text; a catalog whose only source is SSAS gets `calendar_unavailable` for `window`, `index` and `prior_period`. *Needs:* a DAX form of both (`SUMMARIZE` over the date table with `MIN` and `MAX`), which needs item 2 for the days.
4. **No row cap in the DAX text.** The SQL query carries `limit + 1` (`ExecText`) so the source stops at the cap; the DAX query has none, the executor reads up to the cap and the server still computes everything. *Needs:* `TOPN ( n + 1, ... )` around the query when no `limit` is asked.
5. **The executor** is synchronous, opens an ADOMD connection per query, has a fixed 60 second command timeout, and runs under whatever role the connection string's user has: `EVALUATE` cannot write, but nothing makes the role read-only. *Needs:* the async interface (handoff step 4), a connection per host source, a timeout from the connection definition, and a read-only role named in the data source (J16, with Proxy).
6. **`= ""` matches a blank** in DAX (probe O4); SQL does not match a NULL with `''`. Rare; documented, not changed.
7. **Ad hoc aggregates** (`{"as", "agg", "of"}`, query.md form 2) are not executed on any source, so DAX has none either; a measure must exist in the model with `native: {DAX: ...}`.
8. **One model.** The catalog's DAX text names one Tabular database; a catalog that spans two models, or a Power BI or Fabric model over XMLA, has not been tried (handoff section 7).
