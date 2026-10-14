# Phase 201M — A table mapping can state its primary key, and a created table is always creatable

**Status**: Built. See "What was built".
**Plan reference**: none — raised in conversation after running the Arrow Flight SQL driver against a real
proxy. The user chose "on the table mapping" for where the key is marked, and said the nullable problem
must be fixed regardless.

## Why

Two problems, one cause: the source's introspected `ColumnMetadata` is the only place a primary key or a
nullability comes from.

1. **`CREATE TABLE` can fail.** `ProvisioningColumnBuilder` copies `IsNullable` and `IsPrimaryKey` from the
   source, and `CreateTableStatement` rendered a key column as `NULL` when the source called it nullable.
   SQL Server refuses `PRIMARY KEY` over a nullable column, so several tables created from a
   Flight SQL / ODBC / view source failed. Those sources report every column nullable, key or not.
2. **A source with no declared key can't have one.** A view, an ODBC source or the proxy reports no primary
   key (the proxy's metadata has none), so the created target has no `PRIMARY KEY`, the SCD2 natural key
   can't be derived and `KeyReconcile` has nothing to match on. The operator has no way to say what the
   key is.

## Part 1 — built

`CreateTableStatement.Build` renders `NOT NULL` for any column with `IsPrimaryKey`, whatever the source
said. One choke point (`CreateTargetTablePlanner` is its only caller), so the Setup preview and the
unattended `CreateTargetTableIfMissing` path agree. Test: `AKeyColumnTheSourceReportedNullable_IsStillCreatedNotNull`.

## Part 2 — planned

**A mapping carries a key.** `TableMappingConfig.PrimaryKey: List<string>`, source column names (empty = use
whatever the source reports). A set rather than a sequence — see "What was built". It sits beside `ColumnMappings` rather than as a flag on
each row: a key is one ordered set, and the editor, YAML and validation all want it as such. This follows
`DefaultSegmenting` as the precedent for a per-mapping list the mapping editor owns.

**One resolution function, used everywhere.** A new `MappingKey.Resolve(sourceColumns, mapping)` returns the
effective key columns: the mapping's list if non-empty, else the source's `IsPrimaryKey` columns in source
order. Callers that read `IsPrimaryKey` directly today switch to it, so a preview and a run can't disagree:

- `ProvisioningColumnBuilder.Build` — sets `IsPrimaryKey` from the resolved key, and forces key columns
  non-nullable (belt and braces with part 1).
- `NaturalKeyDerivation.Derive` — derives the SCD2 natural key from the resolved key.
- `KeyReconcileReader` and `ProvisioningService.InferNaturalKey` — same.

**Validation** (`ConfigValidation`): every named column must exist in the mapping's source columns and be
mapped into the target (a key column the mapping doesn't carry can't be matched on); no duplicates.

**Editor**: a "Primary key" control on the mapping — the mapped columns as a multi-select/orderable list,
pre-filled with the source's own key when it has one, with the same pattern as the existing natural-key
control (`naturalKey.ts`). Setting it shows in the Setup card's `CREATE TABLE` preview.

**Flight SQL**: nothing in the driver changes. It can't report keys the proxy doesn't expose (proxy issue
#2 asks for correct nullability); the mapping-level key is the fix on this side.

## Not decided

- Whether `PrimaryKey` should also accept target column names (a renamed key column). Plan: source names
  only, translated through `ColumnMappings` the way segment columns already are.
- Whether an operator-stated key is also used for a target that already exists (no — it only affects
  creation and key derivation; an existing target's own keys are never altered).

## Tests

`MappingKey.Resolve` (stated beats reported; empty falls back; ordering; unmapped column), validation,
`ProvisioningColumnBuilder` with a stated key, an end-to-end `CREATE TABLE` preview for a source reporting
all-nullable columns and no key.

## What was built

- `TableMappingConfig.PrimaryKey` (YAML round-trips; test in `CachedColumnsYamlRoundTripTests`).
- `MappingKey.WithKey` (`Drivers.Abstractions`): with a stated key, exactly those columns are keys and
  non-nullable; the source's own flags are replaced, not merged. It is applied at each place a key was read
  from source column metadata: the executor's reader call, preview request, provisioning and natural-key
  derivation; the API's provisioning plan, natural-key inference and preview; and `ValidateKeyReconcilePairing`.
  This is read-time, not written into the cached `SourceColumns`, so a metadata refresh can't drop it.
- `ConfigValidation.ValidatePrimaryKey`, called from `ConfigRepository` beside `ValidateSources`: no duplicates,
  every column mapped.
- Editor: a **Primary key** card on the Column mapping tab (checkboxes over the mapped source columns, shows
  what the source reports, "use the source's own key" to clear). `docs/getting-started.md` documents it.

**Follow-up (key order):** the first cut treated the key as a set, which was wrong — a key's order is its index
order. `ColumnMetadata`, `CachedColumn`, `ProvisioningColumn` and `CreateTableColumn` now carry a 1-based
`KeyOrdinal` (0 = unknown, falls back to column order). `MappingKey.WithKey` sets it from the stated order,
`MappingKey.InKeyOrder` is the one way to list a key, and `CREATE TABLE`, the SCD2 natural key, key reconcile
and the target shape use it. SQL Server (`sys.index_columns.key_ordinal`) and the information-schema catalogs
report the source's own order. Oracle and JDBC still report none (column order). The Primary key card lists a
composite key in order with up/down buttons.

**Not verified:** the editor card was typechecked and the web unit tests pass, but it was not driven in a
browser. The executor/API call-site changes are covered by the unit tests of the helpers and by building; the
SQL Server-backed integration tests can't run in this sandbox, so the `key_ordinal` catalog query is untested.
