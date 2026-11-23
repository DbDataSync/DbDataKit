# SYNCAUTHORING-10 (the rest): work in progress

## Built so far

- P12 (answered by the owner, 2026-11-22: C): a missing table or column (mapped, in a filter, a relationship's table) is an error; type and key
  findings are warnings; notes stay info. In the docs and out of the subject file.
- Types read through Sync's code: `MappingColumnReader` over the cache, each driver's Sync dialect (built-in engines, `drivers/<id>/driver.yaml`
  through `DescriptorDialect`, ANSI first for Flight SQL), compared by `FidelityRules` after `CanonicalTypes.ToShared` (new, `DbDataKit.Sync.Core`).
- YAML drivers' and Flight SQL's (no `dialect`) source types and filters.
- The key check of the generic readers and writers (`TriggerAudit`, `KeyReconcile`, `ChangeApply`, `KeyReconcileDelete`, `Scd2`,
  `KeyReconcileScd2Close`); an engine's own kind is a note.

## Not built

- The key check of the engine-specific readers and writers: waits on SYNCAUTHORING-P19 (the row says so).

## Open questions

- SYNCAUTHORING-P13 is not confirmed by the owner. The switch is built; the proposal stays in the subject file with a "Built since" paragraph saying what
  goes back if the owner chooses otherwise. Example: Ana's mapping reads `amount decimal(18,2)` from SQL Server into an `integer`; today the check reads
  the source type with Sync's `MsSqlDialect` and warns "may not fit".

## Defaults taken (each is a proposal in the subject file)

- SYNCAUTHORING-P20: a YAML driver's filter is read with the ANSI lexer and the descriptor's quoting. Example: `[region code] > 3` on a `bracket`
  driver reports `region code` when the cache does not have it.
- SYNCAUTHORING-P21: a target not in the cache is key-checked as provisioning would create it. Example: a keyless source into a new target under
  `Watermark` warns that `ChangeApply` would refuse it.
- SYNCAUTHORING-P22: three rules of the engine copied in the check (Flight SQL's ANSI-first type reading, `KeyReconcileDeleteWriter`'s and
  `Scd2Writer`'s refusals). Example: if `FlightSqlDialect` changes its order, the check does not follow until someone moves the rule.
