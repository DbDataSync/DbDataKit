# DbData.Transfer.SqlServer

`SqlServerBulkWriter` (`IBulkWriter`): `SqlBulkCopy` fed from one `ChainedBatchReader` over all batches. No `DataTable`; memory is one batch plus SqlBulkCopy's buffers. Columns map by name; `EnableStreaming` is on.

Values are adapted for SqlBulkCopy (`ParameterValues`): `sbyte` to `short`, `ushort` to `int`, `uint` to `long`, `ulong` to `decimal`, `DateOnly` to `DateTime`, `TimeOnly` to `TimeSpan`.

Options (`SqlServerBulkSettings.From`): `BatchSize` (0 = one batch), `Timeout` (zero = unlimited, else rounded up to seconds), `KeepIdentity`, `KeepNulls`, `Transaction` = `PerBatch` (internal transaction per batch; refuses an external one) or `Single` (needs the `SqlTransaction` passed to the writer), `Progress` (every batch).

Pass the writer to `WriteGate.BulkWriteAsync` so the write is checked and logged. This file and `AdoWriteExecutor` are the only ones allowed to touch driver commands (see `InvariantTests`).

Tested offline: settings mapping, reader and adaptation. Live (`DBDATA_TEST_SQLSERVER`): 10,000-row streaming copy and the conformance cases. Not measured: throughput versus the old DataTable path (see `shared/benchmarks`; the stub only compares the in-memory feeding cost). `Microsoft.Data.SqlClient` 7.1.1 pulls `System.Security.Cryptography.Pkcs` 9.0.18 (known advisories); `Directory.Packages.props` pins it to 10.0.13.
