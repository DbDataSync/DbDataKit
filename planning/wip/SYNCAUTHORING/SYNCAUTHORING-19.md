# SYNCAUTHORING-19: Sync uses the shared types (work in progress)

## Built

- **Shared types** (`DbDataKit.Dialect`, `DbDataKit.Dialect.Abstractions`, `DbDataKit.Dialect.Arrow`), each case with tests in `TypeTests`,
  `AbstractionsTests`, `ArrowTests` and the golden table:
  - unsigned integers: no change was needed; `An_unsigned_integer_keeps_its_range` pins the answers;
  - SYNCAUTHORING-P23 (answered A): MySQL writes a time, timestamp or zoned timestamp with no precision with 6 digits;
  - SYNCAUTHORING-P24 (answered A): a decimal with no precision is PostgreSQL `numeric`, `decimal(38,9)` elsewhere with a note;
  - SYNCAUTHORING-P25 (answered A): `CanonicalType.Fixed` (`binary(fixed,16)`), SQL Server and MySQL `binary(n)` and `rowversion`, the Arrow map
    reads `FixedSizeBinary` exactly.
  The outcomes are in `Shared/src/DbDataKit.Dialect/README.md`, "Types with no stated size, unsigned integers and fixed-length binary".
- **Sync on the shared type, adapter first.** `SqlDialect.ReadColumnType` / `WriteColumnType` work on the shared type; the built-in dialects
  (SQL Server, PostgreSQL, MySQL, Oracle, DuckDB, JDBC) answer them from their engine's shared table through `DialectTypes`, and nothing else.
  `SharedTypes` and `CanonicalTypes` are deleted. Sync's `CanonicalType` (with a new `IsUnsigned`) stays as the shape scripts, `driver.csx` files and
  `ProvisioningColumn` see, converted both ways without changing an answer (SYNCAUTHORING-P28). The generic value binder and the Workspace's
  offline check read through `ReadColumnType`.
- Release notes: `docs/release-notes-pending.md`, "Column types: one set of answers for every product".

## Left

- **SYNCAUTHORING-P26 (Oracle time), awaiting the owner.** Where it slots in:
  - A: the shared `oracle.yaml` row `interval day(0) to second({s})` ← `time({s})` becomes `timestamp({s})`, lossy, with the note
    `OracleDialect.WriteColumnType` writes today; then that override goes and Oracle writes through `DialectTypes` like the others.
  - B: the override goes, and `oracle.yaml` gains a reading row `interval day(0) to second({s})` → `time({s})`.
- **SYNCAUTHORING-P27 (ANSI names), awaiting the owner.** `AnsiDialect` keeps its own table until then (it now widens an unsigned integer it receives).
  Under B: a new `dialects/flightsql.yaml` that extends `ansi` with the names `ArrowValues.NativeTypeName` gives and the writes of `AnsiDialect`;
  `AnsiDialect` then reads and writes through `DialectTypes` over it, its hand-written table goes, and the ANSI time with no precision is decided there.
- **Removing Sync's `CanonicalType`** is SYNCAUTHORING-P28; the task row's "no type named `CanonicalType` remains" depends on its answer.
- `ScriptTestService`'s small table (which sample value a script test generates for a column, with no connection) is unchanged: it picks sample
  values and decides no column type. The list (item 10) said it would move; it stays unless the owner wants it moved.

## Tests run on this branch

- Shared: Dialect 193 (golden regenerated), Dialect.Abstractions 26, Dialect.Arrow 37, Dialect.Conformance 134 (15 skipped: live engines),
  Transfer 330, Metadata 133 (3 skipped), DataModel 63 (10 skipped), Sync.Core 530 plus 12 new, Sync.Drivers.Abstractions 68, Sync.Drivers.Descriptor 27,
  Sync.Drivers.Generic 330, Sync.Scripting.Abstractions 3, Authoring.Server 4.
- DbDataSync unit suite (`Category!=Integration`, JDBC skipped): every project passes; DuckDB's 4 unsigned tests changed with the listed case (now 21).
- Workspace: 205, 22 skipped (engines, a built `dbdatasync`/`dbdatabuild`), including `WorkspaceSyncOfflineCheckTests`.
- Build unit: 3426.
- Gateway: builds; its tests ran with 150 failures, every one an `IOException` from the machine's inotify limit (128 instances, shared with other
  agents) or a start that failed on it; none touches a type.
- Builds of the Store, Runner and console filters succeed.

## Defaults taken

Written as SYNCAUTHORING-P28 (the column type scripts see), SYNCAUTHORING-P29 (interval and list columns) and SYNCAUTHORING-P30 (the JDBC driver's
types) in the subject file.
