# What the SSAS (DAX) path lacks, and what was fixed

Reviewed 2026-10-27 (plan 19 J22) by reading `DaxEmitter`, `SsasExecutor` and the catalog loader against the SQL emitter, and by sending probes to a real Analysis Services (`SsasProbe`, workflow `ssas_probe`; results below are measured, not read). [ssas.md](ssas.md) has the earlier findings (runs 1 to 12).

## Fixed in J22

| Finding | Evidence | Fix |
| --- | --- | --- |
| **`not_equals`, `not_contains` and a `not_equals` on a measure kept a blank.** SQL's `<>` and `NOT LIKE` drop a NULL; DAX's `NOT ( col IN { ... } )` and `NOT CONTAINSSTRING` keep it | probes O1 and O3: with `West` made blank, `NOT ( [r] IN { "East" } )` returned the blank row. (A comparison such as `>` already dropped it, O2.) No sample fixture could see it: the model has no blank | the emitter writes `NOT ISBLANK ( col ) && ...` (probe O5 returned only `North`); fixtures `level2/28`, `29`, `30` run `not_equals`, `not_contains` and `starts_with` on the SSAS source, on the stand-in and on real DAX |
| **Agent-written native text ran as anything the command accepts.** `ExecuteNativeText` handed the text to ADOMD, which also runs MDX, DMX and DMV queries | probe P1: an MDX `SELECT ... FROM [Model]` returned the model's value through the same path | `SsasExecutor.IsDaxQuery`: only text that starts (after whitespace and comments) with `EVALUATE` or `DEFINE` is run; anything else is `NotSupportedException` before a connection is opened. `SsasExecutorGuardTests` |

## Fixed in J23 (2026-10-27)

| Finding | Fix |
| --- | --- |
| **The model had no date column**, so `period_to_date`, written-date `between` and a day's prior-year counterpart did not apply to the SSAS source | The model's `Date` table is at day grain (the seed's `dim_date`: the date, the fiscal levels, `Date Prior Year`); `Budget` has a `Month Start` column and relates to `Date[Date]`, a month's budget being dated on its first day. The catalog's SSAS source binds `date.calendar_date` and the prior-year date to it, and `dim_month` is gone (`dim_budget_date`, over the same `dim_date` rows). Fixtures `level4/128` to `130` |
| **Calendars were read from SQL sources only** (`BuildLevelRanges`, `BuildPairs`: the order and days of each period, the pairs of a counterpart or a hierarchy), so a catalog whose only date table was the model's got `calendar_unavailable` | `DaxEmitter.BuildLevelRanges` (`SUMMARIZECOLUMNS` of the level with `MIN` and `MAX` of the date) and `BuildPairs` (`DISTINCT` of `SELECTCOLUMNS` over the dimension table); the engine asks the first source that has the level and the date in one table, SQL Server or Analysis Services, with the SQL text as the stand-in's twin. A pair read now returns the whole dimension (a month with no budget row is there) instead of the months the fact rows hold. `CalendarFromDaxTests` runs the SSAS fixtures with the SQL date bindings taken away |

## Not done: gaps, with what each needs

1. **Datetime attributes** are refused when the catalog loads (`datetime_unsupported_source`): DAX has no time zone conversion. SQL sources support instants, whole-day filters and buckets from hour to year. *Needs a decision:* a model that stores UTC with a zone table the emitter joins, or buckets precomputed in the model per zone, or leaving it unsupported.
2. ~~**Day-level dates.**~~ Fixed in J23 (see below).
3. ~~**Calendars are read from SQL sources only.**~~ Fixed in J23 (see below).
4. **No row cap in the DAX text.** The SQL query carries `limit + 1` (`ExecText`) so the source stops at the cap; the DAX query has none, the executor reads up to the cap and the server still computes everything. *Needs:* `TOPN ( n + 1, ... )` around the query when no `limit` is asked.
5. **The executor** is synchronous, opens an ADOMD connection per query, has a fixed 60 second command timeout, and runs under whatever role the connection string's user has: `EVALUATE` cannot write, but nothing makes the role read-only. *Needs:* the async interface (handoff step 4), a connection per host source, a timeout from the connection definition, and a read-only role named in the data source (J16, with Proxy).
6. **`= ""` matches a blank** in DAX (probe O4); SQL does not match a NULL with `''`. Rare; documented, not changed.
7. **Ad hoc aggregates** (`{"as", "agg", "of"}`, query.md form 2) are not executed on any source, so DAX has none either; a measure must exist in the model with `native: {DAX: ...}`.
8. **One model.** The catalog's DAX text names one Tabular database; a catalog that spans two models, or a Power BI or Fabric model over XMLA, has not been tried (handoff section 7).
