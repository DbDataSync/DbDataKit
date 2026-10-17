# DbDataKit.Transfer.Abstractions

Contracts for moving rows between engines (Plan 07). No SQL, no drivers; depends on `DbDataKit.Dialect.Abstractions` (canonical types) and `DbDataKit.Dialect.Arrow`.

## Contract

| Type | Role |
|---|---|
| `TransferSchema` / `TransferColumn` | Ordered, case-insensitively unique columns: name, `CanonicalType`, nullability, key flag. |
| `DataBatch` | Bounded rows + schema. `AsDataReader()` (forward-only, fresh per call, no DataTable) and `AsArrow()`. `RowDataBatch` (row-major) and `ArrowDataBatch` (wraps a `RecordBatch`) implement it. |
| `ClrContract` | The CLR type each canonical type carries in a batch (`Int8` signed = `sbyte`, `Date` = `DateOnly`, `Time` = `TimeOnly`, tz timestamp = `DateTimeOffset`, ...). One contract between the converter and the writers. |
| `IDataSource`, `IPositionedDataSource`, `IChangePosition` | Streaming read; the position is opaque text produced and read only by the same source (Sync's CT/CDC/LSN/slot readers stay in Sync). |
| `IBulkWriter`, `WriteTarget`, `BulkOptions` | One bulk route per engine. Options: batch size, timeout, `TransactionMode` (None, PerBatch, Single), keep identity/nulls, progress. |
| `IStagingProvider`, `StagingTable`, `StagingLifecycle` | Create / load / drop a staging table. |
| `IApplyStrategy`, `ApplyRequest`, `StatementSet`, `SqlStatement`, `SqlParameter`, `DeleteGuard` | A strategy turns a request plus a dialect into ordered, parameterised statements. Values only ever appear as `SqlParameter.Value`. |
| `WriteIntent`, `WriteKind`, `WriteRisk` | What a statement is for; the gate enforces permissions with it. |
| `ITransferStateStore`, `ITransferVerifier` | Interfaces only. Sync's State DB and Build's tracking tables stay product code; no shared schema. |
| `TransferException` family | `ValueConversionException` (column + failure code), `DeleteGuardExceededException` (counts), `GateRefusedException`, `StrategyNotSupportedException`. Messages name columns and codes, never values. |

## Decisions

- **Reader-shaped interchange first, Arrow as adapter.** Both products already feed bulk routes from readers; `AsArrow()` is for Proxy-style consumers. Arrow-first is a later decision that needs a measured benchmark (see `shared/benchmarks`).
- `AsArrow()` on a row batch uses the Plan 05 canonical-to-Arrow table (microsecond timestamps, Decimal128 up to 38 digits, guid as text). A guid column therefore comes back as `string` from `ArrowDataBatch`.
- Not done: Arrow-first / Parquet staging, list types (opaque in the canonical model).
